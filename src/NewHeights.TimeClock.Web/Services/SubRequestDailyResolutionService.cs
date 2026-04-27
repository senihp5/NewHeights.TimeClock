using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase C (2026-04-27) hosted service. Once per day at RunHour (default
/// 6 AM local), walks every TcSubRequest whose StartDate is today and
/// whose status hasn't been finalized, then resolves each one:
///
///   SubConfirmed       → AbsenceApproved  (auto-approve, notify teacher + supervisor)
///   PartiallyAssigned  → no change         (notify supervisor with Take-Over link)
///   AwaitingSub /
///   Submitted /
///   SubAssigned        → Cancelled        (auto-cancel, notify teacher + supervisor)
///
/// Emergency requests created within EmergencyGraceHours are skipped so
/// same-morning emergencies don't get auto-cancelled before their cascade
/// has a chance to land coverage.
///
/// Idempotency: the in-memory LastRunDate guard prevents double-runs in
/// normal operation. The underlying status transitions are also defensive
/// (only flip from the expected source state), so a duplicate tick after
/// app restart is a no-op rather than a bug.
/// </summary>
public class SubRequestDailyResolutionService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SubRequestDailyResolutionOptions _options;
    private readonly ILogger<SubRequestDailyResolutionService> _logger;

    private DateOnly _lastRunDate = DateOnly.MinValue;

    private const string NavyBlue = "#2D2D6D";
    private const string Gold     = "#F7C72C";
    private const string Green    = "#059669";
    private const string Amber    = "#f59e0b";
    private const string Red      = "#dc2626";

    public SubRequestDailyResolutionService(
        IServiceScopeFactory scopeFactory,
        IOptions<SubRequestDailyResolutionOptions> options,
        ILogger<SubRequestDailyResolutionService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialDelay = TimeSpan.FromMinutes(Math.Max(1, _options.InitialDelayMinutes));
        var scanInterval = TimeSpan.FromMinutes(Math.Max(1, _options.ScanIntervalMinutes));

        _logger.LogInformation(
            "SubRequestDailyResolutionService started. Enabled={Enabled}, RunHour={Hour}, scan every {Interval}.",
            _options.Enabled, _options.RunHour, scanInterval);

        try { await Task.Delay(initialDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_options.Enabled && ShouldRunNow())
                {
                    await RunSweepAsync(stoppingToken);
                    _lastRunDate = DateOnly.FromDateTime(DateTime.Now);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SubRequestDailyResolutionService: tick threw — will retry.");
            }

            try { await Task.Delay(scanInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    private bool ShouldRunNow()
    {
        var now = DateTime.Now;
        var today = DateOnly.FromDateTime(now);

        // Already swept today.
        if (_lastRunDate >= today) return false;

        // Wall-clock gating: run between RunHour and RunHour+2 so a quick
        // restart shortly after RunHour still picks up. Outside that window
        // wait for tomorrow's RunHour.
        return now.Hour >= _options.RunHour && now.Hour < _options.RunHour + 2;
    }

    private async Task RunSweepAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory    = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TimeClockDbContext>>();
        var emailService = scope.ServiceProvider.GetRequiredService<IEmailService>();
        var smsService   = scope.ServiceProvider.GetRequiredService<ISmsService>();
        var audit        = scope.ServiceProvider.GetRequiredService<IAuditService>();

        await using var ctx = await dbFactory.CreateDbContextAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.Today);
        var now = DateTime.Now;

        var requests = await ctx.TcSubRequests
            .Include(r => r.RequestingEmployee).ThenInclude(e => e!.Staff)
            .Include(r => r.AssignedSubEmployee).ThenInclude(e => e!.Staff)
            .Include(r => r.Assignments).ThenInclude(a => a.SubEmployee).ThenInclude(e => e!.Staff)
            .Include(r => r.Campus)
            .Where(r => r.StartDate == today
                     && r.Status != SubRequestStatus.AbsenceApproved
                     && r.Status != SubRequestStatus.Cancelled
                     && r.Status != SubRequestStatus.Denied)
            .ToListAsync(ct);

        _logger.LogInformation(
            "SubRequestDailyResolution sweep at {Now}: {Count} request(s) up for resolution.",
            now, requests.Count);

        int autoApproved = 0, autoCancelled = 0, partialNotified = 0, skipped = 0;

        foreach (var req in requests)
        {
            // Emergency grace window — skip recent emergencies so the
            // cascade gets a fair shot before we auto-cancel.
            if (req.IsEmergency && (now - req.CreatedDate).TotalHours < _options.EmergencyGraceHours)
            {
                _logger.LogInformation(
                    "Skipping recent emergency request {Id} (age {Age}h < grace {Grace}h)",
                    req.SubRequestId, (now - req.CreatedDate).TotalHours, _options.EmergencyGraceHours);
                skipped++;
                continue;
            }

            try
            {
                switch (req.Status)
                {
                    case SubRequestStatus.SubConfirmed:
                        await AutoApproveAsync(req, ctx, emailService, smsService, audit, ct);
                        autoApproved++;
                        break;

                    case SubRequestStatus.PartiallyAssigned:
                        await NotifyPartialAsync(req, ctx, emailService, smsService, audit, ct);
                        partialNotified++;
                        break;

                    case SubRequestStatus.AwaitingSub:
                    case SubRequestStatus.Submitted:
                    case SubRequestStatus.SubAssigned:
                        await AutoCancelAsync(req, ctx, emailService, smsService, audit, ct);
                        autoCancelled++;
                        break;

                    default:
                        // Should be filtered out by the query, but defensive.
                        _logger.LogWarning(
                            "Unexpected status {Status} on request {Id} during daily resolution sweep — skipping.",
                            req.Status, req.SubRequestId);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to resolve request {Id} ({Status}) during daily sweep. Continuing.",
                    req.SubRequestId, req.Status);
            }
        }

        _logger.LogInformation(
            "SubRequestDailyResolution sweep complete: autoApproved={Approved}, autoCancelled={Cancelled}, partialNotified={Partial}, skipped={Skipped}.",
            autoApproved, autoCancelled, partialNotified, skipped);
    }

    // ── SubConfirmed → AbsenceApproved ─────────────────────────────────

    private async Task AutoApproveAsync(
        TcSubRequest req, TimeClockDbContext ctx,
        IEmailService email, ISmsService sms, IAuditService audit, CancellationToken ct)
    {
        var now = DateTime.Now;
        req.Status = SubRequestStatus.AbsenceApproved;
        req.SupervisorApprovedDate = now;
        req.SupervisorApprovedBy = "system@auto-approve";
        req.ModifiedDate = now;
        await ctx.SaveChangesAsync(ct);

        await audit.LogActionAsync(
            actionCode: AuditActions.SubOutreach.AutoApproved,
            entityType: AuditEntityTypes.SubRequest,
            entityId: req.SubRequestId.ToString(),
            newValues: new
            {
                req.SubRequestId,
                Reason = "Sub confirmed all periods; supervisor never gave final approval; auto-approved at run-hour.",
                AutoApprovedAt = now.ToString("yyyy-MM-dd HH:mm:ss")
            },
            deltaSummary:
                $"Auto-approved sub request {req.SubRequestId} on day-of (sub confirmed, supervisor approval timed out).",
            source: AuditSource.System,
            employeeId: req.RequestingEmployeeId);

        var teacherName = TeacherName(req);
        var subjectsLine = SubsCoveringLine(req);
        var dateLine = req.StartDate.ToString("dddd, MMM d, yyyy");
        var campusName = req.Campus?.CampusName ?? "New Heights";
        var shortDate  = req.StartDate.ToString("MMM d");

        var teacher    = req.RequestingEmployee;
        var supervisor = await ResolveSupervisorEmployeeAsync(ctx, req, ct);

        var subject = $"Auto-approved: your sub for {dateLine}";
        var html = BuildEmailHtml(
            color: Green,
            headline: "Sub Request Auto-Approved",
            body:
                $"<p>Good morning, <strong>{HtmlEnc(teacherName)}</strong>.</p>" +
                $"<p>Your sub request for <strong>{HtmlEnc(dateLine)}</strong> at " +
                $"<strong>{HtmlEnc(campusName)}</strong> was <strong>auto-approved</strong> at the " +
                $"start of the school day because a substitute had already accepted every requested " +
                $"period and supervisor approval was still pending.</p>" +
                subjectsLine,
            req: req, periodsForEmail: req.PeriodsNeeded ?? "—");

        var smsBody = $"New Heights: Your sub for {shortDate} is auto-approved (covered by all subs already). Reply STOP to opt out.";

        await TrySendBothAsync(email, sms, teacher,    subject, html, smsBody);
        await TrySendBothAsync(email, sms, supervisor, subject, html, smsBody);
    }

    // ── PartiallyAssigned → notify supervisor with Take-Over link ──────

    private async Task NotifyPartialAsync(
        TcSubRequest req, TimeClockDbContext ctx,
        IEmailService email, ISmsService sms, IAuditService audit, CancellationToken ct)
    {
        var supervisor = await ResolveSupervisorEmployeeAsync(ctx, req, ct);
        if (supervisor == null)
        {
            _logger.LogWarning(
                "PartiallyAssigned request {Id} has no resolvable supervisor — skipping day-of notice.",
                req.SubRequestId);
            return;
        }

        var teacherName = TeacherName(req);
        var dateLine = req.StartDate.ToString("dddd, MMM d, yyyy");
        var shortDate = req.StartDate.ToString("MMM d");
        var campusName = req.Campus?.CampusName ?? "New Heights";

        // Compute remaining periods so the email tells the supervisor
        // exactly which slots still need coverage.
        var needed = ParsePeriods(req.PeriodsNeeded);
        var covered = req.Assignments
            .SelectMany(a => ParsePeriods(a.PeriodsCovered))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = needed.Where(p => !covered.Contains(p)).OrderBy(p => p).ToList();

        var takeoverUrl = $"{_options.PortalBaseUrl.TrimEnd('/')}/supervisor/sub-calendar";

        var subject = $"⚠️ Partial coverage today — {teacherName} ({dateLine})";
        var html = BuildEmailHtml(
            color: Amber,
            headline: "Sub Request Needs Your Attention",
            body:
                $"<p><strong>{HtmlEnc(teacherName)}</strong>'s sub request for " +
                $"<strong>{HtmlEnc(dateLine)}</strong> at <strong>{HtmlEnc(campusName)}</strong> " +
                $"is <strong>partially covered</strong>.</p>" +
                SubsCoveringLine(req) +
                (remaining.Count > 0
                    ? $"<p><strong>Still uncovered:</strong> {HtmlEnc(string.Join(",", remaining))}</p>"
                    : "") +
                $"<p style='margin-top:1rem;'><a href='{takeoverUrl}' style='display:inline-block;background:{NavyBlue};color:#fff;padding:.6rem 1.1rem;border-radius:6px;text-decoration:none;font-weight:600;'>Open Sub Calendar &rarr; Take Over</a></p>" +
                $"<p style='color:#6b7280;font-size:.85rem;'>From the Sub Calendar, click the cell for this request and use <em>Take Over Request</em> to cancel pending outreach, flip to emergency, and manually assign a sub.</p>",
            req: req, periodsForEmail: req.PeriodsNeeded ?? "—");

        var smsBody = $"New Heights: Sub coverage for {teacherName} on {shortDate} is INCOMPLETE. Open Sub Calendar to take over. Reply STOP to opt out.";
        if (smsBody.Length > 320) smsBody = smsBody.Substring(0, 320);

        await TrySendBothAsync(email, sms, supervisor, subject, html, smsBody);

        await audit.LogActionAsync(
            actionCode: AuditActions.SubOutreach.PartialDayOf,
            entityType: AuditEntityTypes.SubRequest,
            entityId: req.SubRequestId.ToString(),
            newValues: new
            {
                req.SubRequestId,
                SupervisorEmail = supervisor.Email,
                RemainingPeriods = string.Join(",", remaining),
                CoveredPeriods = string.Join(",", covered.OrderBy(p => p))
            },
            deltaSummary:
                $"Day-of partial-coverage notice sent to supervisor for sub request {req.SubRequestId} " +
                $"(remaining: {string.Join(",", remaining)}).",
            source: AuditSource.System,
            employeeId: req.RequestingEmployeeId);
    }

    // ── AwaitingSub / Submitted / SubAssigned → Cancelled ─────────────

    private async Task AutoCancelAsync(
        TcSubRequest req, TimeClockDbContext ctx,
        IEmailService email, ISmsService sms, IAuditService audit, CancellationToken ct)
    {
        var now = DateTime.Now;
        var priorStatus = req.Status;
        req.Status = SubRequestStatus.Cancelled;
        req.ModifiedDate = now;

        // Cancel any AWAITING outreach so late-responders can't poach
        // a slot on a request the system just gave up on.
        var awaiting = await ctx.TcSubOutreach
            .Where(o => o.SubRequestId == req.SubRequestId && o.ResponseStatus == "AWAITING")
            .ToListAsync(ct);
        foreach (var o in awaiting)
        {
            o.ResponseStatus = "CANCELLED_BY_AUTO";
            o.RespondedAt = now;
        }
        await ctx.SaveChangesAsync(ct);

        await audit.LogActionAsync(
            actionCode: AuditActions.SubOutreach.AutoCancelled,
            entityType: AuditEntityTypes.SubRequest,
            entityId: req.SubRequestId.ToString(),
            newValues: new
            {
                req.SubRequestId,
                PriorStatus = priorStatus.ToString(),
                CancelledAwaitingOutreachCount = awaiting.Count,
                AutoCancelledAt = now.ToString("yyyy-MM-dd HH:mm:ss")
            },
            deltaSummary:
                $"Auto-cancelled sub request {req.SubRequestId} on day-of (was {priorStatus}; " +
                $"no sub accepted by deadline). Cancelled {awaiting.Count} pending outreach row(s).",
            source: AuditSource.System,
            employeeId: req.RequestingEmployeeId);

        var teacherName = TeacherName(req);
        var dateLine = req.StartDate.ToString("dddd, MMM d, yyyy");
        var shortDate = req.StartDate.ToString("MMM d");
        var campusName = req.Campus?.CampusName ?? "New Heights";

        var teacher    = req.RequestingEmployee;
        var supervisor = await ResolveSupervisorEmployeeAsync(ctx, req, ct);

        var subject = $"Sub request cancelled — no sub accepted ({dateLine})";
        var html = BuildEmailHtml(
            color: Red,
            headline: "Sub Request Auto-Cancelled",
            body:
                $"<p>The sub request for <strong>{HtmlEnc(teacherName)}</strong> on " +
                $"<strong>{HtmlEnc(dateLine)}</strong> at <strong>{HtmlEnc(campusName)}</strong> " +
                $"was automatically cancelled this morning because no substitute accepted " +
                $"by the start-of-day deadline.</p>" +
                $"<p>If coverage is still needed today, contact your campus manager directly " +
                $"or submit a new request flagged as <em>Emergency</em>.</p>",
            req: req, periodsForEmail: req.PeriodsNeeded ?? "—");

        var smsBody = $"New Heights: Sub request for {shortDate} was auto-cancelled — no substitute accepted by deadline. Contact your campus manager. Reply STOP to opt out.";
        if (smsBody.Length > 320) smsBody = smsBody.Substring(0, 320);

        await TrySendBothAsync(email, sms, teacher,    subject, html, smsBody);
        await TrySendBothAsync(email, sms, supervisor, subject, html, smsBody);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Send to a TcEmployee via BOTH email and SMS when each channel is
    /// applicable. Email fires when the recipient has an Email; SMS fires
    /// when the SMS service is enabled, the recipient has a Phone, and they
    /// haven't opted out. Failures on either channel are logged but never
    /// thrown — daily-resolution sweep continues to the next request even
    /// if one notification can't be delivered. Recipient = null is a no-op.
    /// </summary>
    private async Task TrySendBothAsync(
        IEmailService email, ISmsService sms,
        TcEmployee? recipient,
        string subject, string html, string smsBody)
    {
        if (recipient == null) return;

        if (!string.IsNullOrWhiteSpace(recipient.Email))
        {
            try { await email.SendEmailAsync(recipient.Email!, subject, html); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Daily-resolution email send failed for employee {Id}",
                    recipient.EmployeeId);
            }
        }

        if (sms.IsEnabled
            && !recipient.SmsOptedOut
            && !string.IsNullOrWhiteSpace(recipient.Phone))
        {
            try { await sms.SendAsync(recipient.Phone!, smsBody); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Daily-resolution SMS send failed for employee {Id}",
                    recipient.EmployeeId);
            }
        }
    }

    private static string TeacherName(TcSubRequest req)
    {
        var staff = req.RequestingEmployee?.Staff;
        if (staff != null) return $"{staff.FirstName} {staff.LastName}".Trim();
        return req.RequestingEmployee?.DisplayName
            ?? req.RequestingEmployee?.Email
            ?? "the teacher";
    }

    private static string SubsCoveringLine(TcSubRequest req)
    {
        if (req.Assignments == null || req.Assignments.Count == 0) return "";
        var lines = req.Assignments
            .OrderBy(a => a.AcceptedAt)
            .Select(a =>
            {
                var name = a.SubEmployee?.Staff?.FullName
                        ?? a.SubEmployee?.DisplayName
                        ?? a.SubEmployee?.Email
                        ?? "Unknown sub";
                return $"<li><strong>{HtmlEnc(name)}</strong> · {HtmlEnc(a.PeriodsCovered ?? "—")}</li>";
            });
        return $"<p style='margin:.6rem 0 .25rem;'>Subs covering:</p><ul style='margin-top:0;'>{string.Concat(lines)}</ul>";
    }

    /// <summary>
    /// Resolve the full supervisor TcEmployee (not just email) so the daily
    /// notification can dispatch SMS too. Same precedence as
    /// SubOutreachService.TryNotifyStakeholdersAsync — SupervisorApprovedBy
    /// email match first, then fallback to the teacher's Entra-linked
    /// supervisor on TcEmployees.
    /// </summary>
    private static async Task<TcEmployee?> ResolveSupervisorEmployeeAsync(
        TimeClockDbContext ctx, TcSubRequest req, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(req.SupervisorApprovedBy))
        {
            var emailLower = req.SupervisorApprovedBy.Trim().ToLower();
            var sup = await ctx.TcEmployees
                .AsNoTracking()
                .FirstOrDefaultAsync(e =>
                    e.IsActive && e.Email != null && e.Email.ToLower() == emailLower, ct);
            if (sup != null) return sup;
        }

        var teacherId = req.RequestingEmployeeId;
        return await ctx.TcEmployees
            .AsNoTracking()
            .Where(e => e.IsActive)
            .FirstOrDefaultAsync(e => ctx.TcEmployees.Any(
                t => t.EmployeeId == teacherId && t.SupervisorEmployeeId == e.EmployeeId), ct);
    }

    private static IEnumerable<string> ParsePeriods(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) yield break;
        foreach (var raw in csv.Split(','))
        {
            var p = raw.Trim().ToUpperInvariant();
            if (p.Length > 0) yield return p;
        }
    }

    private static string HtmlEnc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string BuildEmailHtml(
        string color, string headline, string body,
        TcSubRequest req, string periodsForEmail)
    {
        var dates = req.StartDate == req.EndDate
            ? req.StartDate.ToString("MMM d, yyyy")
            : $"{req.StartDate:MMM d}–{req.EndDate:MMM d, yyyy}";
        var campus = HtmlEnc(req.Campus?.CampusName ?? "New Heights");
        return $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  <h2 style='color: {color};'>{HtmlEnc(headline)}</h2>
  {body}
  <table style='border-collapse: collapse; margin: .9rem 0; font-size:.92rem;'>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Campus:</td><td>{campus}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Dates:</td><td>{HtmlEnc(dates)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Periods:</td><td>{HtmlEnc(periodsForEmail)}</td></tr>
  </table>
  <p style='color:#6b7280;font-size:.8rem;'>This is an automated message from the New Heights Staff Portal day-of sub resolution sweep.</p>
</div>";
    }
}
