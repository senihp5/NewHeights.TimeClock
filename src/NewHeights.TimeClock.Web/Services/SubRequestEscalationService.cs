using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase 9c background job. Periodically scans TC_SubRequests for
/// AwaitingSub rows that have been sitting without outreach for longer
/// than SubRequestEscalationOptions.StaleThresholdHours and notifies the
/// requesting teacher's supervisor (email + SMS when enabled) so the
/// admin can dispatch outreach manually.
///
/// Idempotency: stamps TcSubRequest.EscalatedAt on each escalation and
/// skips rows whose EscalatedAt is within ReEscalationIntervalHours.
///
/// Pattern matches StaleTokenExpiryService (scoped services resolved via
/// IServiceScopeFactory per tick, exceptions swallowed + logged).
/// </summary>
public class SubRequestEscalationService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SubRequestEscalationService> _logger;
    private readonly SubRequestEscalationOptions _options;

    public SubRequestEscalationService(
        IServiceScopeFactory scopeFactory,
        ILogger<SubRequestEscalationService> logger,
        IOptions<SubRequestEscalationOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = TimeSpan.FromMinutes(Math.Max(1, _options.InitialDelayMinutes));
        var runInterval  = TimeSpan.FromHours(Math.Max(1, _options.ScanIntervalHours));

        _logger.LogInformation(
            "SubRequestEscalationService started. Enabled={Enabled}, initialDelay={InitialDelay}, interval={Interval}, staleThresholdHours={Stale}, reEscalationHours={ReEsc}.",
            _options.Enabled, initialDelay, runInterval,
            _options.StaleThresholdHours, _options.ReEscalationIntervalHours);

        try
        {
            await Task.Delay(initialDelay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled)
                    await RunOnceAsync(stoppingToken);
                else
                    _logger.LogDebug("SubRequestEscalationService: Enabled=false — skipping tick.");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SubRequestEscalationService: tick threw — will retry after interval.");
            }

            try
            {
                await Task.Delay(runInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("SubRequestEscalationService stopping.");
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TimeClockDbContext>>();
        var email     = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var sms       = scope.ServiceProvider.GetRequiredService<ISmsService>();
        var audit     = scope.ServiceProvider.GetRequiredService<IAuditService>();

        var now          = DateTime.Now;
        var staleCutoff  = now.AddHours(-_options.StaleThresholdHours);
        var reEscCutoff  = now.AddHours(-_options.ReEscalationIntervalHours);

        await using var context = await dbFactory.CreateDbContextAsync(ct);

        // Phase 9c: AwaitingSub rows whose CreatedDate is older than the stale
        // threshold AND whose last escalation (if any) is older than the
        // re-escalation interval. Order oldest-first so we clear the backlog
        // first on the initial enable.
        var stale = await context.TcSubRequests
            .Include(r => r.RequestingEmployee)
                .ThenInclude(e => e.Staff)
            .Include(r => r.RequestingEmployee)
                .ThenInclude(e => e.Supervisor)
                    .ThenInclude(s => s!.Staff)
            .Include(r => r.Campus)
            .Where(r => r.Status == SubRequestStatus.AwaitingSub
                     && r.CreatedDate < staleCutoff
                     && (r.EscalatedAt == null || r.EscalatedAt < reEscCutoff))
            .OrderBy(r => r.CreatedDate)
            .ToListAsync(ct);

        if (stale.Count == 0)
        {
            _logger.LogDebug("SubRequestEscalationService: nothing stale this tick.");
            return;
        }

        _logger.LogInformation(
            "SubRequestEscalationService: {Count} AwaitingSub request(s) need escalation.",
            stale.Count);

        var escalated = 0;
        var skipped = 0;

        foreach (var request in stale)
        {
            ct.ThrowIfCancellationRequested();

            var supervisor = request.RequestingEmployee?.Supervisor;
            if (supervisor == null)
            {
                _logger.LogWarning(
                    "SubRequestEscalationService: request {Id} has no supervisor linked — cannot escalate. Run EmployeeSync to populate manager attribute.",
                    request.SubRequestId);
                skipped++;
                continue;
            }

            var sent = await TryEscalateAsync(request, supervisor, email, sms);

            request.EscalatedAt = now;
            request.ModifiedDate = now;

            await audit.LogActionAsync(
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
                    StaleSinceHours      = Math.Round((now - request.CreatedDate).TotalHours, 1),
                    EmailSent            = sent.EmailSent,
                    SmsAttempted         = sent.SmsAttempted,
                    SmsDelivered         = sent.SmsDelivered
                },
                deltaSummary:
                    $"Escalated AwaitingSub request #{request.SubRequestId} to {supervisor.DisplayName ?? supervisor.Email} "
                    + $"(stale {Math.Round((now - request.CreatedDate).TotalHours, 1)}h). "
                    + $"Email={(sent.EmailSent ? "sent" : "skipped")}, Sms={(sent.SmsDelivered ? "sent" : sent.SmsAttempted ? "failed" : "skipped")}.",
                source: AuditSource.System,
                employeeId: request.RequestingEmployeeId,
                campusId: request.CampusId,
                ct: ct);

            escalated++;
        }

        await context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "SubRequestEscalationService: escalated {Escalated} request(s), skipped {Skipped} (no supervisor).",
            escalated, skipped);
    }

    private async Task<EscalationSendResult> TryEscalateAsync(
        TcSubRequest request,
        TcEmployee supervisor,
        IEmailService email,
        ISmsService sms)
    {
        var result = new EscalationSendResult();

        var teacherName = request.RequestingEmployee?.Staff?.FullName
                       ?? request.RequestingEmployee?.Email
                       ?? "the teacher";
        var campusName = request.Campus?.CampusName ?? "New Heights";
        var dates = request.StartDate == request.EndDate
            ? request.StartDate.ToString("MMM d")
            : $"{request.StartDate:MMM d}-{request.EndDate:MMM d}";
        var staleHours = Math.Round((DateTime.Now - request.CreatedDate).TotalHours, 0);

        // SMS body — ≤ 320 chars (2 segments).
        var smsBody =
            $"New Heights: Absence #{request.SubRequestId} from {teacherName} for {dates} at {campusName} "
            + $"has been waiting {staleHours}h for sub outreach. Please assist at /supervisor/sub-requests.";
        if (smsBody.Length > 320) smsBody = smsBody.Substring(0, 320);

        // Email — HTML.
        var subject = $"Action needed: sub outreach not dispatched for {teacherName} ({dates})";
        var html = BuildEscalationHtml(request, supervisor, teacherName, campusName, dates, staleHours);

        // SMS path — gated by IsEnabled, SmsOptedOut, non-empty phone.
        if (sms.IsEnabled
         && !supervisor.SmsOptedOut
         && !string.IsNullOrWhiteSpace(supervisor.Phone))
        {
            result.SmsAttempted = true;
            try
            {
                var smsResult = await sms.SendAsync(supervisor.Phone!, smsBody);
                result.SmsDelivered = smsResult.Delivered;
                if (!smsResult.Delivered)
                {
                    _logger.LogWarning(
                        "SubRequestEscalationService: SMS skipped or failed for supervisor {EmployeeId} on request {Id}: {Reason}",
                        supervisor.EmployeeId, request.SubRequestId, smsResult.ErrorReason);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SubRequestEscalationService: SMS threw for supervisor {EmployeeId} on request {Id}",
                    supervisor.EmployeeId, request.SubRequestId);
            }
        }
        else
        {
            _logger.LogDebug(
                "SubRequestEscalationService: SMS skipped (IsEnabled={IsEnabled}, SmsOptedOut={OptOut}, HasPhone={HasPhone}) for supervisor {EmployeeId} on request {Id}",
                sms.IsEnabled, supervisor.SmsOptedOut, !string.IsNullOrWhiteSpace(supervisor.Phone),
                supervisor.EmployeeId, request.SubRequestId);
        }

        // Email path — always attempt when supervisor has email.
        if (!string.IsNullOrWhiteSpace(supervisor.Email))
        {
            try
            {
                result.EmailSent = await email.SendEmailAsync(supervisor.Email!, subject, html);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SubRequestEscalationService: email threw for supervisor {EmployeeId} on request {Id}",
                    supervisor.EmployeeId, request.SubRequestId);
            }
        }
        else
        {
            _logger.LogWarning(
                "SubRequestEscalationService: supervisor {EmployeeId} has no email — cannot escalate request {Id} via email.",
                supervisor.EmployeeId, request.SubRequestId);
        }

        return result;
    }

    private static string BuildEscalationHtml(
        TcSubRequest request,
        TcEmployee supervisor,
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
    {System.Net.WebUtility.HtmlEncode(teacherName)}. This is an automated escalation;
    the teacher still has the ability to invite a sub directly from their queue.
  </p>
</div>";
    }

    private class EscalationSendResult
    {
        public bool EmailSent { get; set; }
        public bool SmsAttempted { get; set; }
        public bool SmsDelivered { get; set; }
    }
}
