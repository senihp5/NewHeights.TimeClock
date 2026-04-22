using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase A background job (migration 048). Each tick (hourly by default)
/// finds TcSubRequests stuck in <see cref="SubRequestStatus.PartiallyAssigned"/>
/// past the ThresholdHours window and sends the requesting employee's
/// supervisor a nudge — "request #N still has uncovered periods P2, P5
/// after 24h; please follow up."
///
/// Dedup via <c>TcSubRequest.PartialStallAlertSentAt</c>. Once a request is
/// alerted, we stamp that column; <see cref="SubOutreachService.ProcessAcceptAsync"/>
/// resets it back to NULL on any new partial accept so "progress was made,
/// restart the stall clock" semantics hold.
///
/// Failure handling: email or SMS exceptions are logged but do NOT prevent
/// stamping PartialStallAlertSentAt — otherwise a bad supervisor inbox
/// would cause the service to spin, nagging the audit log every tick.
/// </summary>
public class PartialStallAlertService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly PartialStallAlertOptions _options;
    private readonly ILogger<PartialStallAlertService> _logger;

    public PartialStallAlertService(
        IServiceScopeFactory scopeFactory,
        IOptions<PartialStallAlertOptions> options,
        ILogger<PartialStallAlertService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = TimeSpan.FromMinutes(Math.Max(1, _options.InitialDelayMinutes));
        var runInterval  = TimeSpan.FromMinutes(Math.Max(1, _options.ScanIntervalMinutes));

        _logger.LogInformation(
            "PartialStallAlertService started. Enabled={Enabled}, initial delay {InitialDelay}, scan interval {Interval}, threshold {Threshold}h.",
            _options.Enabled, initialDelay, runInterval, _options.ThresholdHours);

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
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "PartialStallAlertService: tick threw — will retry after interval.");
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

        _logger.LogInformation("PartialStallAlertService stopping.");
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var dbFactory = sp.GetRequiredService<IDbContextFactory<TimeClockDbContext>>();
        var emailService = sp.GetRequiredService<IEmailService>();
        var smsService = sp.GetRequiredService<ISmsService>();
        var audit = sp.GetRequiredService<IAuditService>();

        var thresholdHours = Math.Max(1, _options.ThresholdHours);
        var cutoff = DateTime.Now.AddHours(-thresholdHours);

        await using var db = await dbFactory.CreateDbContextAsync(stoppingToken);

        // Stuck requests: PartiallyAssigned + no progress since cutoff + not yet alerted.
        var stalled = await db.TcSubRequests
            .Include(r => r.Campus)
            .Include(r => r.RequestingEmployee).ThenInclude(e => e.Staff)
            .Include(r => r.RequestingEmployee).ThenInclude(e => e.Supervisor).ThenInclude(s => s!.Staff)
            .Include(r => r.Assignments)
            .Where(r => r.Status == SubRequestStatus.PartiallyAssigned
                     && r.ModifiedDate < cutoff
                     && r.PartialStallAlertSentAt == null)
            .ToListAsync(stoppingToken);

        if (stalled.Count == 0)
        {
            _logger.LogDebug("PartialStallAlertService tick: no stalled partial requests.");
            return;
        }

        int alerted = 0, skippedNoSupervisor = 0;
        foreach (var request in stalled)
        {
            if (stoppingToken.IsCancellationRequested) break;

            var supervisor = request.RequestingEmployee?.Supervisor;
            if (supervisor == null
             || (string.IsNullOrWhiteSpace(supervisor.Email)
                 && string.IsNullOrWhiteSpace(supervisor.Phone)))
            {
                _logger.LogWarning(
                    "PartialStallAlertService: request {Id} (teacher {EmpId}) has no supervisor with contact info — skipping.",
                    request.SubRequestId, request.RequestingEmployeeId);
                skippedNoSupervisor++;
                // Stamp anyway so we don't re-scan this one every tick. If the
                // supervisor link is fixed later, the next accept on this request
                // will reset the dedup.
                request.PartialStallAlertSentAt = DateTime.Now;
                continue;
            }

            // Compute remaining periods for the alert body.
            var needed = ParsePeriodSet(request.PeriodsNeeded);
            var covered = request.Assignments
                .SelectMany(a => ParsePeriodSet(a.PeriodsCovered))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remaining = needed.Except(covered, StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => p)
                .ToList();

            // Who accepted what so far (for context in the alert body).
            var coveredBy = request.Assignments
                .OrderBy(a => a.AcceptedAt)
                .Select(a => new
                {
                    Periods = a.PeriodsCovered,
                    Sub = FormatSubName(a),
                })
                .ToList();

            var teacherName  = request.RequestingEmployee?.Staff?.FullName
                            ?? request.RequestingEmployee?.DisplayName
                            ?? "the requesting teacher";
            var supervisorFirst = supervisor.Staff?.FirstName
                               ?? supervisor.Staff?.FullName?.Split(' ').FirstOrDefault()
                               ?? supervisor.DisplayName
                               ?? "there";
            var campusName = request.Campus?.CampusName ?? "New Heights";
            var dateRange = request.StartDate == request.EndDate
                ? request.StartDate.ToString("MMM d")
                : $"{request.StartDate:MMM d} – {request.EndDate:MMM d}";
            var hoursStuck = (int)Math.Floor((DateTime.Now - request.ModifiedDate).TotalHours);

            // Email + SMS (both gated appropriately).
            var (subject, html) = BuildStallEmail(
                supervisorFirst, teacherName, campusName, dateRange,
                request.SubRequestId, remaining, coveredBy.Select(x => $"{x.Sub}: {x.Periods}").ToList(),
                hoursStuck);

            var smsBody = BuildStallSms(teacherName, campusName, dateRange, remaining, hoursStuck);

            bool emailOk = false, smsOk = false;
            if (!string.IsNullOrWhiteSpace(supervisor.Email))
            {
                try
                {
                    emailOk = await emailService.SendEmailAsync(supervisor.Email!, subject, html);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "PartialStallAlertService: email to supervisor {SupId} failed for request {Id}.",
                        supervisor.EmployeeId, request.SubRequestId);
                }
            }

            if (smsService.IsEnabled
             && !supervisor.SmsOptedOut
             && !string.IsNullOrWhiteSpace(supervisor.Phone))
            {
                try
                {
                    var result = await smsService.SendAsync(supervisor.Phone!, smsBody, stoppingToken);
                    smsOk = result.Delivered;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "PartialStallAlertService: SMS to supervisor {SupId} failed for request {Id}.",
                        supervisor.EmployeeId, request.SubRequestId);
                }
            }

            // Stamp regardless of send outcome so we don't spin. If every channel
            // failed, the audit row will show it and an operator can follow up.
            request.PartialStallAlertSentAt = DateTime.Now;
            alerted++;

            await audit.LogActionAsync(
                actionCode: AuditActions.SubOutreach.PartialStallAlerted,
                entityType: AuditEntityTypes.SubRequest,
                entityId: request.SubRequestId.ToString(),
                newValues: new
                {
                    request.SubRequestId,
                    request.RequestingEmployeeId,
                    SupervisorEmployeeId = supervisor.EmployeeId,
                    HoursStuck = hoursStuck,
                    Remaining = string.Join(",", remaining),
                    EmailDelivered = emailOk,
                    SmsDelivered = smsOk
                },
                deltaSummary: $"Supervisor {supervisor.EmployeeId} alerted: request {request.SubRequestId} has stalled {hoursStuck}h at PartiallyAssigned (remaining: {string.Join(",", remaining)}).",
                source: AuditSource.System,
                employeeId: supervisor.EmployeeId,
                ct: stoppingToken);
        }

        await db.SaveChangesAsync(stoppingToken);

        _logger.LogInformation(
            "PartialStallAlertService tick: alerted {Alerted}, skipped-no-supervisor {Skipped}, total scanned {Total}.",
            alerted, skippedNoSupervisor, stalled.Count);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static HashSet<string> ParsePeriodSet(string? csv)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return result;
        foreach (var raw in csv.Split(','))
        {
            var p = raw.Trim().ToUpperInvariant();
            if (p.Length > 0) result.Add(p);
        }
        return result;
    }

    private static string FormatSubName(TcSubRequestAssignment a)
    {
        return a.SubEmployee?.Staff?.FullName
            ?? a.SubEmployee?.DisplayName
            ?? a.SubEmployee?.Email
            ?? $"Employee {a.SubEmployeeId}";
    }

    private static (string subject, string html) BuildStallEmail(
        string supervisorFirst, string teacherName, string campusName, string dateRange,
        long subRequestId, List<string> remaining, List<string> coveredByLines, int hoursStuck)
    {
        var subject = $"Sub request stalled — {teacherName} ({dateRange}) — action needed";

        var coveredHtml = coveredByLines.Count == 0
            ? "<li><em>No periods covered yet.</em></li>"
            : string.Join("", coveredByLines.Select(l =>
                $"<li>{System.Net.WebUtility.HtmlEncode(l)}</li>"));

        var remainingText = remaining.Count > 0
            ? string.Join(", ", remaining)
            : "(no specific periods specified)";

        var html = $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 620px; color: #1f2937;'>
  <h2 style='color: #b45309;'>Sub Request Stalled</h2>
  <p>Hi {System.Net.WebUtility.HtmlEncode(supervisorFirst)},</p>
  <p>
    Substitute request <strong>#{subRequestId}</strong> for <strong>{System.Net.WebUtility.HtmlEncode(teacherName)}</strong>
    at <strong>{System.Net.WebUtility.HtmlEncode(campusName)}</strong> ({System.Net.WebUtility.HtmlEncode(dateRange)})
    has been partially covered for <strong>{hoursStuck} hour(s)</strong> with no further progress.
  </p>

  <div style='background:#fef3c7; border:1px solid #fde68a; border-left:4px solid #f5b81c; border-radius:6px; padding:.75rem 1rem; margin:.75rem 0;'>
    <strong style='color:#92400e;'>Remaining uncovered periods:</strong>
    <span style='margin-left:.4rem; color:#78350f; font-weight:600;'>{System.Net.WebUtility.HtmlEncode(remainingText)}</span>
  </div>

  <p><strong>Currently covered by:</strong></p>
  <ul style='margin:.25rem 0 .75rem 1.2rem; padding:0; color:#334155;'>
    {coveredHtml}
  </ul>

  <p>
    Please review and either extend the outreach to additional subs, manually assign someone,
    or reach out to {System.Net.WebUtility.HtmlEncode(teacherName)} about alternatives.
  </p>

  <p style='margin-top:1rem;'>
    <a href='https://clock.newheightsed.com/supervisor/sub-requests'
       style='display:inline-block; padding:10px 20px; background:#1e3a5f; color:#fff;
              text-decoration:none; border-radius:6px; font-weight:500;'>Open Sub Requests</a>
  </p>

  <p style='color:#6b7280;font-size:0.85rem;'>
    You received this because you are the supervisor of the requesting employee.
    This is an automated one-time alert per stall window — you won't be nagged again
    unless a new partial accept restarts the clock.
  </p>
</div>";

        return (subject, html);
    }

    private static string BuildStallSms(
        string teacherName, string campusName, string dateRange,
        List<string> remaining, int hoursStuck)
    {
        var remainingText = remaining.Count > 0
            ? string.Join(",", remaining)
            : "unspecified periods";

        var body = $"New Heights: Sub req for {teacherName} ({campusName} {dateRange}) stalled {hoursStuck}h. Remaining: {remainingText}. Please review. Reply STOP to opt out.";
        const int SmsMaxLength = 320;
        if (body.Length > SmsMaxLength) body = body.Substring(0, SmsMaxLength);
        return body;
    }
}
