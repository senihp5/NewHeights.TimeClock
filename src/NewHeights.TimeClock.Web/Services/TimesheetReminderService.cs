using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase D3 background job. Each tick (hourly by default) walks the current
/// pay period + candidate employees/supervisors and fires email reminders
/// to prompt timesheet submission / approval before the payroll deadline.
///
/// Reminder windows:
///   EMPLOYEE_48H        hours-to-deadline in (24, 48], hourly/PT/sub with
///                       no submitted TcPayPeriodSummary for the period.
///   EMPLOYEE_24H        hours-to-deadline in (0, 24], same audience.
///   SUPERVISOR_DEADLINE hours-to-deadline &lt;= 0, supervisors whose direct
///                       reports still have Pending summaries.
///
/// Dedup via TC_TimesheetReminderLog unique index. A successful send writes
/// DeliveryStatus='SENT'; a failed send writes 'FAILED' + ErrorMessage so a
/// subsequent tick can retry safely (the unique index prevents the SENT row
/// from re-firing but a FAILED row could be removed manually or re-retried
/// by re-opening the ReminderType semantics later if needed).
///
/// Once ACS SMS goes live (toll-free verification approval), the same
/// helper can also fan out SMS to hourly/PT/sub recipients with Phone set
/// and SmsOptedOut = false. Left as a follow-up.
/// </summary>
public class TimesheetReminderService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimesheetReminderOptions _options;
    private readonly ILogger<TimesheetReminderService> _logger;

    public TimesheetReminderService(
        IServiceScopeFactory scopeFactory,
        IOptions<TimesheetReminderOptions> options,
        ILogger<TimesheetReminderService> logger)
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
            "TimesheetReminderService started. Enabled={Enabled}, initial delay {InitialDelay}, scan interval {Interval}.",
            _options.Enabled, initialDelay, runInterval);

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
                    "TimesheetReminderService: tick threw — will retry after interval.");
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

        _logger.LogInformation("TimesheetReminderService stopping.");
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var dbFactory = sp.GetRequiredService<IDbContextFactory<TimeClockDbContext>>();
        var payPeriodService = sp.GetRequiredService<IPayPeriodService>();
        var emailService = sp.GetRequiredService<IEmailService>();

        // Resolve the current pay period from the DB (not the estimated
        // fallback). If estimated, skip — we can't reliably attach a dedup row
        // to a PayPeriodId of 0.
        var period = await payPeriodService.GetCurrentPayPeriodAsync();
        if (period.IsEstimated || period.PayPeriodId == 0)
        {
            _logger.LogDebug("TimesheetReminderService: skipping, no imported pay period covers today.");
            return;
        }

        var now = DateTime.Now;
        var deadline = period.EmployeeDeadline.ToDateTime(new TimeOnly(17, 0));
        var hoursToDeadline = (deadline - now).TotalHours;

        _logger.LogInformation(
            "TimesheetReminderService tick. Period={PeriodId} {Name}, hoursToDeadline={Hours:F1}",
            period.PayPeriodId, period.PeriodName, hoursToDeadline);

        // EMPLOYEE_48H — in (24, 48] hour window
        if (hoursToDeadline > 24 && hoursToDeadline <= 48)
        {
            await FireEmployeeReminders(
                dbFactory, emailService, period,
                reminderType: "EMPLOYEE_48H",
                deadlineDateOnly: period.EmployeeDeadline,
                stoppingToken);
        }

        // EMPLOYEE_24H — in (0, 24] hour window
        if (hoursToDeadline > 0 && hoursToDeadline <= 24)
        {
            await FireEmployeeReminders(
                dbFactory, emailService, period,
                reminderType: "EMPLOYEE_24H",
                deadlineDateOnly: period.EmployeeDeadline,
                stoppingToken);
        }

        // SUPERVISOR_DEADLINE — on/after deadline
        if (hoursToDeadline <= 0)
        {
            await FireSupervisorReminders(
                dbFactory, emailService, period,
                deadlineDateOnly: period.EmployeeDeadline,
                stoppingToken);
        }
    }

    private async Task FireEmployeeReminders(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IEmailService emailService,
        PayPeriodInfo period,
        string reminderType,
        DateOnly deadlineDateOnly,
        CancellationToken ct)
    {
        using var db = await dbFactory.CreateDbContextAsync(ct);

        // Employees who are hourly-like AND either have no summary for this
        // period, or have a Pending summary. Those are the ones who still
        // need to submit.
        var candidates = await db.TcEmployees
            .AsNoTracking()
            .Include(e => e.Staff)
            .Include(e => e.Supervisor).ThenInclude(s => s!.Staff)
            .Where(e => e.IsActive
                     && (e.EmployeeType == EmployeeType.HourlyStaff
                      || e.EmployeeType == EmployeeType.HourlyPartTime
                      || e.EmployeeType == EmployeeType.Substitute)
                     && !string.IsNullOrEmpty(e.Email))
            .ToListAsync(ct);

        if (candidates.Count == 0) return;

        // Pull existing summaries + dedup rows in bulk.
        var employeeIds = candidates.Select(e => e.EmployeeId).ToList();

        var submittedIds = (await db.TcPayPeriodSummaries
            .AsNoTracking()
            .Where(s => s.PayPeriodId == period.PayPeriodId
                     && employeeIds.Contains(s.EmployeeId)
                     && s.ApprovalStatus != ApprovalStatus.Pending)
            .Select(s => s.EmployeeId)
            .ToListAsync(ct)).ToHashSet();

        var alreadySentIds = (await db.TcTimesheetReminderLogs
            .AsNoTracking()
            .Where(r => r.PayPeriodId == period.PayPeriodId
                     && r.ReminderType == reminderType
                     && employeeIds.Contains(r.EmployeeId)
                     && r.DeliveryStatus == "SENT")
            .Select(r => r.EmployeeId)
            .ToListAsync(ct)).ToHashSet();

        int sent = 0, skipped = 0;
        foreach (var emp in candidates)
        {
            if (ct.IsCancellationRequested) break;
            if (submittedIds.Contains(emp.EmployeeId)) { skipped++; continue; }
            if (alreadySentIds.Contains(emp.EmployeeId)) { skipped++; continue; }

            var employeeName = emp.Staff != null
                ? (emp.Staff.FirstName + " " + emp.Staff.LastName)
                : (emp.DisplayName ?? emp.Email ?? "Employee");
            var supervisorName = emp.Supervisor?.Staff != null
                ? (emp.Supervisor.Staff.FirstName + " " + emp.Supervisor.Staff.LastName)
                : (emp.Supervisor?.DisplayName ?? "your supervisor");

            bool delivered = false;
            string? errorMsg = null;
            try
            {
                delivered = await emailService.SendTimesheetReminderAsync(
                    emp.Email!, employeeName, supervisorName, deadlineDateOnly);
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                _logger.LogError(ex,
                    "Employee reminder email failed. EmployeeId={EmpId}, Type={Type}",
                    emp.EmployeeId, reminderType);
            }

            // Write the dedup row either way so we don't spin on a broken inbox.
            db.TcTimesheetReminderLogs.Add(new TcTimesheetReminderLog
            {
                PayPeriodId = period.PayPeriodId,
                EmployeeId = emp.EmployeeId,
                ReminderType = reminderType,
                SentAt = DateTime.Now,
                DeliveryStatus = delivered ? "SENT" : "FAILED",
                ErrorMessage = errorMsg
            });

            if (delivered) sent++;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "TimesheetReminderService: {Type} sent {Sent} / skipped {Skipped} / candidates {Count}",
            reminderType, sent, skipped, candidates.Count);
    }

    private async Task FireSupervisorReminders(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IEmailService emailService,
        PayPeriodInfo period,
        DateOnly deadlineDateOnly,
        CancellationToken ct)
    {
        using var db = await dbFactory.CreateDbContextAsync(ct);
        const string reminderType = "SUPERVISOR_DEADLINE";

        // Supervisors = TcEmployees that appear in SupervisorEmployeeId of an
        // active direct report whose summary is Pending.
        var pendingByManager = await db.TcEmployees
            .AsNoTracking()
            .Where(e => e.IsActive && e.SupervisorEmployeeId != null)
            .Where(e => !db.TcPayPeriodSummaries.Any(s =>
                s.PayPeriodId == period.PayPeriodId
                && s.EmployeeId == e.EmployeeId
                && s.ApprovalStatus != ApprovalStatus.Pending))
            .GroupBy(e => e.SupervisorEmployeeId!.Value)
            .Select(g => new { SupervisorId = g.Key, PendingCount = g.Count() })
            .ToListAsync(ct);

        if (pendingByManager.Count == 0) return;

        var supervisorIds = pendingByManager.Select(x => x.SupervisorId).ToList();

        var supervisors = await db.TcEmployees
            .AsNoTracking()
            .Include(e => e.Staff)
            .Where(e => supervisorIds.Contains(e.EmployeeId)
                     && e.IsActive
                     && !string.IsNullOrEmpty(e.Email))
            .ToListAsync(ct);

        var alreadySentIds = (await db.TcTimesheetReminderLogs
            .AsNoTracking()
            .Where(r => r.PayPeriodId == period.PayPeriodId
                     && r.ReminderType == reminderType
                     && supervisorIds.Contains(r.EmployeeId)
                     && r.DeliveryStatus == "SENT")
            .Select(r => r.EmployeeId)
            .ToListAsync(ct)).ToHashSet();

        int sent = 0, skipped = 0;
        foreach (var sup in supervisors)
        {
            if (ct.IsCancellationRequested) break;
            if (alreadySentIds.Contains(sup.EmployeeId)) { skipped++; continue; }

            var pendingCount = pendingByManager.First(x => x.SupervisorId == sup.EmployeeId).PendingCount;
            var supName = sup.Staff != null
                ? (sup.Staff.FirstName + " " + sup.Staff.LastName)
                : (sup.DisplayName ?? sup.Email ?? "Supervisor");

            bool delivered = false;
            string? errorMsg = null;
            try
            {
                delivered = await emailService.SendSupervisorReminderAsync(
                    sup.Email!, supName, pendingCount, deadlineDateOnly);
            }
            catch (Exception ex)
            {
                errorMsg = ex.Message;
                _logger.LogError(ex,
                    "Supervisor reminder email failed. EmployeeId={EmpId}",
                    sup.EmployeeId);
            }

            db.TcTimesheetReminderLogs.Add(new TcTimesheetReminderLog
            {
                PayPeriodId = period.PayPeriodId,
                EmployeeId = sup.EmployeeId,
                ReminderType = reminderType,
                SentAt = DateTime.Now,
                DeliveryStatus = delivered ? "SENT" : "FAILED",
                ErrorMessage = errorMsg
            });

            if (delivered) sent++;
        }

        await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "TimesheetReminderService: {Type} sent {Sent} / skipped {Skipped} / supervisors {Count}",
            reminderType, sent, skipped, supervisors.Count);
    }
}
