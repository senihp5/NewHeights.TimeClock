using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase 9c per-request escalation. Used by both the background
/// <see cref="SubRequestEscalationService"/> (scheduled 4h scans) and the
/// admin "Escalate Now" button on /supervisor/sub-requests. Single code
/// path guarantees the audit log, stamp semantics, and content builders
/// are identical regardless of trigger.
/// </summary>
public interface ISubRequestEscalator
{
    /// <summary>
    /// Escalate a single AwaitingSub request by email + (when enabled) SMS
    /// to the requesting teacher's supervisor. Stamps EscalatedAt and audits
    /// ABSENCE_ESCALATED. Safe to call even if the request is no longer
    /// AwaitingSub — returns a result with SkipReason populated.
    /// </summary>
    /// <param name="subRequestId">The TcSubRequest.SubRequestId.</param>
    /// <param name="triggerSource">"background" or "manual" — recorded in audit.</param>
    /// <param name="triggerByEmail">Admin email for manual trigger; null for background.</param>
    Task<EscalationResult> EscalateOneAsync(
        long subRequestId,
        string triggerSource,
        string? triggerByEmail = null,
        CancellationToken ct = default);
}

/// <summary>Outcome of a single escalation attempt.</summary>
public class EscalationResult
{
    public bool Success { get; set; }
    public bool EmailSent { get; set; }
    public bool SmsAttempted { get; set; }
    public bool SmsDelivered { get; set; }
    public string? SkipReason { get; set; }
    public string? SupervisorDisplay { get; set; }
}

public class SubRequestEscalator : ISubRequestEscalator
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly IEmailService _emailService;
    private readonly ISmsService _smsService;
    private readonly IAuditService _audit;
    private readonly ILogger<SubRequestEscalator> _logger;

    public SubRequestEscalator(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IEmailService emailService,
        ISmsService smsService,
        IAuditService audit,
        ILogger<SubRequestEscalator> logger)
    {
        _dbFactory = dbFactory;
        _emailService = emailService;
        _smsService = smsService;
        _audit = audit;
        _logger = logger;
    }

    public async Task<EscalationResult> EscalateOneAsync(
        long subRequestId,
        string triggerSource,
        string? triggerByEmail = null,
        CancellationToken ct = default)
    {
        var result = new EscalationResult();

        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var request = await context.TcSubRequests
            .Include(r => r.RequestingEmployee)
                .ThenInclude(e => e.Staff)
            .Include(r => r.RequestingEmployee)
                .ThenInclude(e => e.Supervisor)
                    .ThenInclude(s => s!.Staff)
            .Include(r => r.Campus)
            .FirstOrDefaultAsync(r => r.SubRequestId == subRequestId, ct);

        if (request == null)
        {
            result.SkipReason = "request-not-found";
            return result;
        }

        if (request.Status != SubRequestStatus.AwaitingSub)
        {
            result.SkipReason = $"not-awaitingsub ({request.Status})";
            return result;
        }

        var supervisor = request.RequestingEmployee?.Supervisor;
        if (supervisor == null)
        {
            result.SkipReason = "no-supervisor-linked";
            _logger.LogWarning(
                "EscalateOneAsync: request {Id} has no supervisor linked \u2014 skipping. Run EmployeeSync to populate Entra manager attribute.",
                subRequestId);
            return result;
        }

        var now = DateTime.Now;
        var send = await SendEscalationAsync(request, supervisor);
        result.EmailSent    = send.EmailSent;
        result.SmsAttempted = send.SmsAttempted;
        result.SmsDelivered = send.SmsDelivered;
        result.SupervisorDisplay = supervisor.DisplayName
                                ?? supervisor.Staff?.FullName
                                ?? supervisor.Email;

        request.EscalatedAt = now;
        request.ModifiedDate = now;

        await _audit.LogActionAsync(
            actionCode: AuditActions.Absence.Escalated,
            entityType: AuditEntityTypes.SubRequest,
            entityId: request.SubRequestId.ToString(),
            newValues: new
            {
                request.SubRequestId,
                request.RequestingEmployeeId,
                SupervisorEmployeeId = supervisor.EmployeeId,
                SupervisorEmail      = supervisor.Email,
                Status               = request.Status.ToString(),
                TriggerSource        = triggerSource,
                TriggerBy            = triggerByEmail,
                StaleSinceHours      = Math.Round((now - request.CreatedDate).TotalHours, 1),
                EmailSent            = send.EmailSent,
                SmsAttempted         = send.SmsAttempted,
                SmsDelivered         = send.SmsDelivered
            },
            deltaSummary:
                $"Escalated AwaitingSub request #{request.SubRequestId} to {result.SupervisorDisplay} "
                + $"via {triggerSource}{(triggerByEmail == null ? "" : $" by {triggerByEmail}")}. "
                + $"Email={(send.EmailSent ? "sent" : "skipped")}, "
                + $"Sms={(send.SmsDelivered ? "sent" : send.SmsAttempted ? "failed" : "skipped")}.",
            source: triggerSource == "manual" ? AuditSource.AdminUi : AuditSource.System,
            employeeId: request.RequestingEmployeeId,
            campusId: request.CampusId,
            ct: ct);

        await context.SaveChangesAsync(ct);

        result.Success = true;
        return result;
    }

    private async Task<(bool EmailSent, bool SmsAttempted, bool SmsDelivered)> SendEscalationAsync(
        TcSubRequest request, TcEmployee supervisor)
    {
        var emailSent = false;
        var smsAttempted = false;
        var smsDelivered = false;

        var teacherName = request.RequestingEmployee?.Staff?.FullName
                       ?? request.RequestingEmployee?.Email
                       ?? "the teacher";
        var campusName = request.Campus?.CampusName ?? "New Heights";
        var dates = request.StartDate == request.EndDate
            ? request.StartDate.ToString("MMM d")
            : $"{request.StartDate:MMM d}-{request.EndDate:MMM d}";
        var staleHours = Math.Round((DateTime.Now - request.CreatedDate).TotalHours, 0);

        var smsBody =
            $"New Heights: Absence #{request.SubRequestId} from {teacherName} for {dates} at {campusName} "
            + $"has been waiting {staleHours}h for sub outreach. Please assist at /supervisor/sub-requests.";
        if (smsBody.Length > 320) smsBody = smsBody.Substring(0, 320);

        var subject = $"Action needed: sub outreach not dispatched for {teacherName} ({dates})";
        var html = BuildEscalationHtml(request, teacherName, campusName, dates, staleHours);

        if (_smsService.IsEnabled
         && !supervisor.SmsOptedOut
         && !string.IsNullOrWhiteSpace(supervisor.Phone))
        {
            smsAttempted = true;
            try
            {
                var smsResult = await _smsService.SendAsync(supervisor.Phone!, smsBody);
                smsDelivered = smsResult.Delivered;
                if (!smsResult.Delivered)
                {
                    _logger.LogWarning(
                        "SubRequestEscalator: SMS not delivered for supervisor {EmployeeId} on request {Id}: {Reason}",
                        supervisor.EmployeeId, request.SubRequestId, smsResult.ErrorReason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SubRequestEscalator: SMS threw for supervisor {EmployeeId} on request {Id}",
                    supervisor.EmployeeId, request.SubRequestId);
            }
        }

        if (!string.IsNullOrWhiteSpace(supervisor.Email))
        {
            try
            {
                emailSent = await _emailService.SendEmailAsync(supervisor.Email!, subject, html);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SubRequestEscalator: email threw for supervisor {EmployeeId} on request {Id}",
                    supervisor.EmployeeId, request.SubRequestId);
            }
        }
        else
        {
            _logger.LogWarning(
                "SubRequestEscalator: supervisor {EmployeeId} has no email \u2014 cannot escalate request {Id} via email.",
                supervisor.EmployeeId, request.SubRequestId);
        }

        return (emailSent, smsAttempted, smsDelivered);
    }

    private static string BuildEscalationHtml(
        TcSubRequest request,
        string teacherName,
        string campusName,
        string dates,
        double staleHours)
    {
        var periods = request.PeriodsNeeded ?? "\u2014";
        var session = request.SessionType ?? "\u2014";

        return $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  <h2 style='color: #b45309;'>Sub request needs attention</h2>
  <p>
    <strong>{System.Net.WebUtility.HtmlEncode(teacherName)}</strong>
    submitted an absence request that has been waiting
    <strong>{staleHours:0} hour(s)</strong> without sub outreach being dispatched.
    As the campus admin, please review and either (a) nudge the teacher to invite a sub,
    or (b) dispatch outreach yourself from the admin panel.
  </p>
  <table style='border-collapse: collapse; margin: 0.5rem 0;'>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Request #:</td><td>{request.SubRequestId}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Campus:</td><td>{System.Net.WebUtility.HtmlEncode(campusName)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Dates:</td><td>{System.Net.WebUtility.HtmlEncode(dates)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Session:</td><td>{System.Net.WebUtility.HtmlEncode(session)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Periods:</td><td>{System.Net.WebUtility.HtmlEncode(periods)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Submitted:</td><td>{request.CreatedDate:MMM d, yyyy h:mm tt}</td></tr>
  </table>
  <p>
    <a href='https://clock.newheightsed.com/supervisor/sub-requests'
       style='display:inline-block;background:#2563eb;color:#fff;padding:0.5rem 1rem;border-radius:4px;text-decoration:none;font-weight:500;'>
      Open Sub Requests
    </a>
  </p>
  <p style='color:#6b7280;font-size:0.85rem;'>
    You are receiving this because you are the campus supervisor for
    {System.Net.WebUtility.HtmlEncode(teacherName)}. The teacher can still invite a sub directly from their queue.
  </p>
</div>";
    }
}
