using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Enums;
using NewHeights.TimeClock.Shared.DTOs;

namespace NewHeights.TimeClock.Web.Services;

public interface ITimesheetService
{
    Task<WeeklyTimesheet> GetWeeklyTimesheetAsync(int employeeId, DateOnly weekStartDate);
    Task<PayPeriodTimesheet> GetPayPeriodTimesheetAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd);
    Task<bool> SubmitTimesheetAsync(int employeeId, DateOnly weekEndDate, string submittedBy);
    Task<bool> ApproveTimesheetAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd, string approvedBy, string approverRole);
    /// <summary>
    /// Set the ShortDayReason + ShortDayNote on one daily timecard (migration 052).
    /// Creates the TC_DailyTimecards row if it doesn't exist yet so employees can
    /// annotate a pure non-work day before any punches land. Empty/null reason
    /// clears both fields.
    /// </summary>
    Task SetShortDayReasonAsync(int employeeId, DateOnly workDate, string? reason, string? note, string modifiedBy);
    Task<List<TeamTimesheetSummary>> GetTeamTimesheetsAsync(int supervisorEmployeeId, DateOnly periodStart, DateOnly periodEnd);
    /// <summary>
    /// Admin-scope version of <see cref="GetTeamTimesheetsAsync(int, DateOnly, DateOnly)"/>.
    /// Returns every active hourly / part-time / substitute employee across ALL
    /// supervisors and campuses, with supervisor + campus context populated on
    /// each row so the caller can render/filter. Use when the caller has the
    /// TimeClock.Admin role; regular supervisors must call the scoped version.
    /// </summary>
    Task<List<TeamTimesheetSummary>> GetAllTeamTimesheetsAsync(DateOnly periodStart, DateOnly periodEnd);
    Task<List<PayrollSummary>> GetPayrollSummariesAsync(DateOnly periodStart, DateOnly periodEnd);
    Task RecalculateDailyTimecardAsync(int employeeId, DateOnly workDate);
    Task RecalculateWeeklyOvertimeAsync(int employeeId, DateOnly weekStartDate);
}

public class TimesheetService : ITimesheetService
{
    private readonly IDbContextFactory<TimeClockDbContext> _contextFactory;
    private readonly IAuditService _audit;
    private readonly ILogger<TimesheetService> _logger;
    private const decimal OVERTIME_THRESHOLD = 40m;
    private const int ROUNDING_MINUTES = 15;

    public TimesheetService(
        IDbContextFactory<TimeClockDbContext> contextFactory,
        IAuditService audit,
        ILogger<TimesheetService> logger)
    {
        _contextFactory = contextFactory;
        _audit = audit;
        _logger = logger;
    }

    public async Task<WeeklyTimesheet> GetWeeklyTimesheetAsync(int employeeId, DateOnly weekStartDate)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        // Ensure week starts on Monday
        while (weekStartDate.DayOfWeek != DayOfWeek.Monday)
            weekStartDate = weekStartDate.AddDays(-1);
        
        var weekEndDate = weekStartDate.AddDays(6);

        var employee = await context.TcEmployees
            .Include(e => e.Staff)
            .FirstOrDefaultAsync(e => e.EmployeeId == employeeId);

        if (employee == null)
            return new WeeklyTimesheet { EmployeeId = employeeId };

        var dailyCards = await context.TcDailyTimecards
            .Where(t => t.EmployeeId == employeeId && t.WorkDate >= weekStartDate && t.WorkDate <= weekEndDate)
            .OrderBy(t => t.WorkDate)
            .ToListAsync();

        var punches = await context.TcTimePunches
            .Where(p => p.EmployeeId == employeeId && 
                        p.PunchDateTime.Date >= weekStartDate.ToDateTime(TimeOnly.MinValue) &&
                        p.PunchDateTime.Date <= weekEndDate.ToDateTime(TimeOnly.MaxValue) &&
                        p.PunchStatus == PunchStatus.Active)
            .OrderBy(p => p.PunchDateTime)
            .ToListAsync();

        var timesheet = new WeeklyTimesheet
        {
            EmployeeId = employeeId,
            EmployeeName = employee.Staff?.FullName ?? employee.DisplayName ?? "Unknown",
            WeekStartDate = weekStartDate,
            WeekEndDate = weekEndDate,
            Days = new List<DailyTimesheetEntry>()
        };

        // Build each day
        for (var date = weekStartDate; date <= weekEndDate; date = date.AddDays(1))
        {
            var dayCard = dailyCards.FirstOrDefault(d => d.WorkDate == date);
            var dayPunches = punches.Where(p => DateOnly.FromDateTime(p.PunchDateTime) == date).ToList();

            var entry = new DailyTimesheetEntry
            {
                Date = date,
                DayOfWeek = date.DayOfWeek.ToString(),
                Punches = dayPunches.Select(p => new PunchEntry
                {
                    PunchId = p.PunchId,
                    PunchType = p.PunchType.ToString(),
                    PunchTime = p.PunchDateTime,
                    RoundedTime = p.RoundedDateTime,
                    IsManual = p.IsManualEntry,
                    IsModified = p.IsModified
                }).ToList(),
                TotalHours = dayCard?.TotalHours ?? CalculateDayHours(dayPunches),
                RegularHours = dayCard?.RegularHours ?? 0,
                OvertimeHours = dayCard?.OvertimeHours ?? 0,
                HasException = dayCard?.HasException ?? false,
                ExceptionNotes = dayCard?.ExceptionNotes,
                ApprovalStatus = dayCard?.ApprovalStatus ?? ApprovalStatus.Pending,
                ShortDayReason = dayCard?.ShortDayReason,
                ShortDayNote   = dayCard?.ShortDayNote
            };

            timesheet.Days.Add(entry);
        }

        // Calculate weekly totals
        timesheet.TotalRegularHours = timesheet.Days.Sum(d => d.RegularHours);
        timesheet.TotalOvertimeHours = Math.Max(0, timesheet.Days.Sum(d => d.TotalHours) - OVERTIME_THRESHOLD);
        timesheet.TotalRegularHours = Math.Min(timesheet.Days.Sum(d => d.TotalHours), OVERTIME_THRESHOLD);
        timesheet.TotalHours = timesheet.Days.Sum(d => d.TotalHours);
        timesheet.HasExceptions = timesheet.Days.Any(d => d.HasException);
        timesheet.IsSubmitted = dailyCards.All(d => d.ApprovalStatus != ApprovalStatus.Pending);

        return timesheet;
    }

    public async Task<PayPeriodTimesheet> GetPayPeriodTimesheetAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var employee = await context.TcEmployees
            .Include(e => e.Staff)
            .Include(e => e.Supervisor).ThenInclude(s => s!.Staff)
            .FirstOrDefaultAsync(e => e.EmployeeId == employeeId);

        if (employee == null)
            return new PayPeriodTimesheet { EmployeeId = employeeId };

        var dailyCards = await context.TcDailyTimecards
            .Where(t => t.EmployeeId == employeeId && t.WorkDate >= periodStart && t.WorkDate <= periodEnd)
            .OrderBy(t => t.WorkDate)
            .ToListAsync();

        var summary = await context.TcPayPeriodSummaries
            .FirstOrDefaultAsync(s => s.EmployeeId == employeeId && 
                                      s.PayPeriod.StartDate == periodStart && 
                                      s.PayPeriod.EndDate == periodEnd);

        // Group by week for overtime calculation
        var weeks = new List<WeeklyTimesheet>();
        var currentDate = periodStart;
        
        while (currentDate <= periodEnd)
        {
            var weekStart = currentDate;
            while (weekStart.DayOfWeek != DayOfWeek.Monday && weekStart > periodStart)
                weekStart = weekStart.AddDays(-1);
            
            var weekSheet = await GetWeeklyTimesheetAsync(employeeId, weekStart);
            if (!weeks.Any(w => w.WeekStartDate == weekSheet.WeekStartDate))
                weeks.Add(weekSheet);
            
            currentDate = currentDate.AddDays(7);
        }

        // Filter week data to only include days within the pay period
        var periodRegularHours = weeks.SelectMany(w => w.Days)
            .Where(d => d.Date >= periodStart && d.Date <= periodEnd)
            .Sum(d => d.RegularHours);
        var periodOvertimeHours = weeks.SelectMany(w => w.Days)
            .Where(d => d.Date >= periodStart && d.Date <= periodEnd)
            .Sum(d => d.OvertimeHours);
        var periodTotalHours = weeks.SelectMany(w => w.Days)
            .Where(d => d.Date >= periodStart && d.Date <= periodEnd)
            .Sum(d => d.TotalHours);

        return new PayPeriodTimesheet
        {
            EmployeeId = employeeId,
            EmployeeName = employee.Staff?.FullName ?? employee.DisplayName ?? "Unknown",
            SupervisorName = employee.Supervisor?.Staff?.FullName,
            PeriodStart = periodStart,
            PeriodEnd = periodEnd,
            Weeks = weeks,
            TotalRegularHours = periodRegularHours,
            TotalOvertimeHours = periodOvertimeHours,
            TotalHours = periodTotalHours,
            DaysWorked = dailyCards.Count(d => d.TotalHours > 0),
            ExceptionCount = dailyCards.Count(d => d.HasException),
            EmployeeApprovalStatus = summary?.ApprovalStatus ?? ApprovalStatus.Pending,
            EmployeeApprovedDate = null, // Would need field in summary
            SupervisorApprovalStatus = summary?.SupervisorApprovedBy != null ? ApprovalStatus.Approved : ApprovalStatus.Pending,
            SupervisorApprovedBy = summary?.SupervisorApprovedBy,
            SupervisorApprovedDate = summary?.SupervisorApprovedDate,
            HRApprovalStatus = summary?.HRApprovedBy != null ? ApprovalStatus.Approved : ApprovalStatus.Pending,
            HRApprovedBy = summary?.HRApprovedBy,
            HRApprovedDate = summary?.HRApprovedDate
        };
    }

    public async Task<bool> SubmitTimesheetAsync(int employeeId, DateOnly weekEndDate, string submittedBy)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        
        var weekStart = weekEndDate.AddDays(-6);
        while (weekStart.DayOfWeek != DayOfWeek.Monday)
            weekStart = weekStart.AddDays(-1);

        var dailyCards = await context.TcDailyTimecards
            .Where(t => t.EmployeeId == employeeId && t.WorkDate >= weekStart && t.WorkDate <= weekEndDate)
            .ToListAsync();

        foreach (var card in dailyCards)
        {
            card.ApprovalStatus = ApprovalStatus.Approved; // Employee submitted
            card.ApprovedBy = submittedBy;
            card.ApprovedDate = DateTime.Now;
            card.ModifiedDate = DateTime.Now;
        }

        await context.SaveChangesAsync();
        _logger.LogInformation("Timesheet submitted: Employee={EmployeeId}, Week={WeekEnd}, By={SubmittedBy}",
            employeeId, weekEndDate, submittedBy);

        // Audit: one TIMESHEET_SUBMITTED per submission (not per daily card) to avoid
        // polluting the audit log with 5-7 rows every time someone submits a week.
        // EntityId is a composite logical identifier since the action spans multiple cards.
        await _audit.LogActionAsync(
            actionCode: AuditActions.Timesheet.Submitted,
            entityType: AuditEntityTypes.Timecard,
            entityId: $"{employeeId}:week:{weekEndDate:yyyy-MM-dd}",
            newValues: new
            {
                EmployeeId = employeeId,
                WeekStart = weekStart,
                WeekEnd = weekEndDate,
                CardCount = dailyCards.Count,
                TotalHours = dailyCards.Sum(c => c.TotalHours),
                SubmittedBy = submittedBy
            },
            deltaSummary: $"Submitted timesheet for week ending {weekEndDate:yyyy-MM-dd} ({dailyCards.Count} cards) by {submittedBy}",
            source: AuditSource.AdminUi,
            employeeId: employeeId);

        return true;
    }

    public async Task<bool> ApproveTimesheetAsync(int employeeId, DateOnly periodStart, DateOnly periodEnd, string approvedBy, string approverRole)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var payPeriod = await context.TcPayPeriods
            .FirstOrDefaultAsync(p => p.StartDate == periodStart && p.EndDate == periodEnd);

        if (payPeriod == null)
        {
            payPeriod = new TcPayPeriod
            {
                PeriodName = $"{periodStart:MMM d} - {periodEnd:MMM d, yyyy}",
                StartDate = periodStart,
                EndDate = periodEnd,
                PayDate = periodEnd.Day == 15 ? periodEnd : periodEnd.AddDays(1),
                Status = PayPeriodStatus.Open,
                CreatedDate = DateTime.Now
            };
            context.TcPayPeriods.Add(payPeriod);
            await context.SaveChangesAsync();
        }

        var summary = await context.TcPayPeriodSummaries
            .FirstOrDefaultAsync(s => s.PayPeriodId == payPeriod.PayPeriodId && s.EmployeeId == employeeId);

        if (summary == null)
        {
            var timesheet = await GetPayPeriodTimesheetAsync(employeeId, periodStart, periodEnd);
            summary = new TcPayPeriodSummary
            {
                PayPeriodId = payPeriod.PayPeriodId,
                EmployeeId = employeeId,
                TotalRegularHours = timesheet.TotalRegularHours,
                TotalOvertimeHours = timesheet.TotalOvertimeHours,
                TotalHours = timesheet.TotalHours,
                DaysWorked = timesheet.DaysWorked,
                ExceptionCount = timesheet.ExceptionCount,
                CreatedDate = DateTime.Now
            };
            context.TcPayPeriodSummaries.Add(summary);
        }

        if (approverRole == "Supervisor")
        {
            summary.SupervisorApprovedBy = approvedBy;
            summary.SupervisorApprovedDate = DateTime.Now;
            summary.ApprovalStatus = ApprovalStatus.Approved;
        }
        else if (approverRole == "HR")
        {
            summary.HRApprovedBy = approvedBy;
            summary.HRApprovedDate = DateTime.Now;
            summary.ApprovalStatus = ApprovalStatus.Locked;
        }

        summary.ModifiedDate = DateTime.Now;
        await context.SaveChangesAsync();

        _logger.LogInformation("Timesheet approved: Employee={EmployeeId}, Period={Start}-{End}, By={ApprovedBy}, Role={Role}",
            employeeId, periodStart, periodEnd, approvedBy, approverRole);

        // Audit: branch on approver role. Supervisor approval lands on PAY_SUMMARY with
        // SUPERVISOR_APPROVED; HR approval flips status to Locked with HR_APPROVED.
        // This dual-code split mirrors spec sections 19.1.E and 19.1.F.
        var auditAction = approverRole == "HR"
            ? AuditActions.Payroll.HRApproved
            : AuditActions.Timesheet.SupervisorApproved;

        await _audit.LogActionAsync(
            actionCode: auditAction,
            entityType: AuditEntityTypes.PaySummary,
            entityId: summary.SummaryId.ToString(),
            newValues: new
            {
                summary.SummaryId,
                summary.PayPeriodId,
                summary.EmployeeId,
                summary.TotalRegularHours,
                summary.TotalOvertimeHours,
                summary.TotalHours,
                ApprovalStatus = summary.ApprovalStatus.ToString(),
                summary.SupervisorApprovedBy,
                summary.SupervisorApprovedDate,
                summary.HRApprovedBy,
                summary.HRApprovedDate
            },
            deltaSummary: $"{approverRole} approved pay-period summary for employee {employeeId}, {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd} by {approvedBy}",
            source: AuditSource.AdminUi,
            employeeId: employeeId);

        return true;
    }

    public async Task<List<TeamTimesheetSummary>> GetTeamTimesheetsAsync(int supervisorEmployeeId, DateOnly periodStart, DateOnly periodEnd)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        _logger.LogInformation("GetTeamTimesheets: Looking for supervisor {SupervisorId}, period {Start} to {End}", 
            supervisorEmployeeId, periodStart, periodEnd);

        // Query without the enum filter first to see raw data
        var allEmployees = await context.TcEmployees
            .Include(e => e.Staff)
            .Include(e => e.Supervisor).ThenInclude(s => s!.Staff)
            .Include(e => e.HomeCampus)
            .Where(e => e.SupervisorEmployeeId == supervisorEmployeeId && e.IsActive)
            .ToListAsync();

        _logger.LogInformation("Found {Count} employees with supervisor {Id}", allEmployees.Count, supervisorEmployeeId);
        foreach (var emp in allEmployees)
        {
            _logger.LogInformation("  Employee {Id}: Type={Type}", emp.EmployeeId, emp.EmployeeType);
        }

        // Now filter to hourly staff
        var teamMembers = allEmployees.Where(e => e.EmployeeType == EmployeeType.HourlyStaff
                                                 || e.EmployeeType == EmployeeType.HourlyPartTime
                                                 || e.EmployeeType == EmployeeType.Substitute).ToList();
        _logger.LogInformation("After Hourly/PT/Sub filter: {Count} employees", teamMembers.Count);

        var summaries = new List<TeamTimesheetSummary>();

        foreach (var member in teamMembers)
        {
            var timesheet = await GetPayPeriodTimesheetAsync(member.EmployeeId, periodStart, periodEnd);

            // Visibility rule splits by employee type:
            //   Hourly FT / PT: always show. Zero hours is the actionable
            //     signal — supervisor needs to know who hasn't punched yet
            //     and nudge them before the pay-period deadline.
            //   Substitute: only show when there's at least some recorded
            //     time. Subs only work when an assignment falls their way,
            //     so zero-hour rows for subs are normal noise, not missing
            //     submissions. (Sub-specific timecard approvals live on
            //     /supervisor/sub-timesheets anyway.)
            if (member.EmployeeType == EmployeeType.Substitute && timesheet.TotalHours <= 0)
                continue;

            summaries.Add(new TeamTimesheetSummary
            {
                EmployeeId = member.EmployeeId,
                EmployeeName = member.Staff?.FullName ?? member.DisplayName ?? "Unknown",
                IdNumber = member.IdNumber,
                TotalHours = timesheet.TotalHours,
                RegularHours = timesheet.TotalRegularHours,
                OvertimeHours = timesheet.TotalOvertimeHours,
                DaysWorked = timesheet.DaysWorked,
                ExceptionCount = timesheet.ExceptionCount,
                EmployeeApproved = timesheet.EmployeeApprovalStatus == ApprovalStatus.Approved ||
                                   timesheet.EmployeeApprovalStatus == ApprovalStatus.Locked,
                SupervisorApproved = timesheet.SupervisorApprovalStatus == ApprovalStatus.Approved ||
                                     timesheet.SupervisorApprovalStatus == ApprovalStatus.Locked,
                HasExceptions = timesheet.ExceptionCount > 0,
                ShortDayReasons = timesheet.Weeks
                    .SelectMany(w => w.Days)
                    .Where(d => !string.IsNullOrEmpty(d.ShortDayReason))
                    .Select(d => d.ShortDayReason!)
                    .Distinct()
                    .ToList(),
                SupervisorEmployeeId = member.SupervisorEmployeeId,
                SupervisorName = member.Supervisor?.Staff?.FullName
                              ?? member.Supervisor?.DisplayName
                              ?? member.Supervisor?.Email,
                CampusId = member.HomeCampusId,
                CampusName = member.HomeCampus?.CampusName
            });
        }

        return summaries.OrderBy(s => s.EmployeeName).ToList();
    }

    public async Task<List<TeamTimesheetSummary>> GetAllTeamTimesheetsAsync(DateOnly periodStart, DateOnly periodEnd)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        _logger.LogInformation(
            "GetAllTeamTimesheets (admin scope): period {Start} to {End}",
            periodStart, periodEnd);

        // Every active Hourly / Part-Time / Substitute across all supervisors.
        // Eager-load supervisor + campus so the admin UI can group/filter.
        var teamMembers = await context.TcEmployees
            .Include(e => e.Staff)
            .Include(e => e.Supervisor).ThenInclude(s => s!.Staff)
            .Include(e => e.HomeCampus)
            .Where(e => e.IsActive
                     && (e.EmployeeType == EmployeeType.HourlyStaff
                      || e.EmployeeType == EmployeeType.HourlyPartTime
                      || e.EmployeeType == EmployeeType.Substitute))
            .ToListAsync();

        _logger.LogInformation("GetAllTeamTimesheets: {Count} active hourly/PT/sub employees", teamMembers.Count);

        var summaries = new List<TeamTimesheetSummary>();

        foreach (var member in teamMembers)
        {
            var timesheet = await GetPayPeriodTimesheetAsync(member.EmployeeId, periodStart, periodEnd);

            // Same visibility rule as the scoped method: subs with zero hours
            // are hidden (they only work when an assignment lands); hourly FT/PT
            // always shown so admin sees who hasn't punched before deadline.
            if (member.EmployeeType == EmployeeType.Substitute && timesheet.TotalHours <= 0)
                continue;

            summaries.Add(new TeamTimesheetSummary
            {
                EmployeeId = member.EmployeeId,
                EmployeeName = member.Staff?.FullName ?? member.DisplayName ?? "Unknown",
                IdNumber = member.IdNumber,
                TotalHours = timesheet.TotalHours,
                RegularHours = timesheet.TotalRegularHours,
                OvertimeHours = timesheet.TotalOvertimeHours,
                DaysWorked = timesheet.DaysWorked,
                ExceptionCount = timesheet.ExceptionCount,
                EmployeeApproved = timesheet.EmployeeApprovalStatus == ApprovalStatus.Approved ||
                                   timesheet.EmployeeApprovalStatus == ApprovalStatus.Locked,
                SupervisorApproved = timesheet.SupervisorApprovalStatus == ApprovalStatus.Approved ||
                                     timesheet.SupervisorApprovalStatus == ApprovalStatus.Locked,
                HasExceptions = timesheet.ExceptionCount > 0,
                ShortDayReasons = timesheet.Weeks
                    .SelectMany(w => w.Days)
                    .Where(d => !string.IsNullOrEmpty(d.ShortDayReason))
                    .Select(d => d.ShortDayReason!)
                    .Distinct()
                    .ToList(),
                SupervisorEmployeeId = member.SupervisorEmployeeId,
                SupervisorName = member.Supervisor?.Staff?.FullName
                              ?? member.Supervisor?.DisplayName
                              ?? member.Supervisor?.Email,
                CampusId = member.HomeCampusId,
                CampusName = member.HomeCampus?.CampusName
            });
        }

        return summaries
            .OrderBy(s => s.CampusName ?? "~")
            .ThenBy(s => s.SupervisorName ?? "~")
            .ThenBy(s => s.EmployeeName)
            .ToList();
    }

    public async Task<List<PayrollSummary>> GetPayrollSummariesAsync(DateOnly periodStart, DateOnly periodEnd)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var allHourlyEmployees = await context.TcEmployees
            .Include(e => e.Staff)
            .Include(e => e.Supervisor).ThenInclude(s => s!.Staff)
            .Where(e => e.IsActive && (e.EmployeeType == EmployeeType.HourlyStaff
                                       || e.EmployeeType == EmployeeType.HourlyPartTime
                                       || e.EmployeeType == EmployeeType.Substitute))
            .ToListAsync();

        var summaries = new List<PayrollSummary>();

        foreach (var employee in allHourlyEmployees)
        {
            var timesheet = await GetPayPeriodTimesheetAsync(employee.EmployeeId, periodStart, periodEnd);

            // Only show employees with time recorded in the current pay period.
            // HR doesn't need to see zero-hour rows during payroll review.
            if (timesheet.TotalHours <= 0) continue;

            summaries.Add(new PayrollSummary
            {
                EmployeeId = employee.EmployeeId,
                EmployeeName = employee.Staff?.FullName ?? employee.DisplayName ?? "Unknown",
                IdNumber = employee.IdNumber,
                AscenderEmployeeId = employee.AscenderEmployeeId,
                SupervisorName = employee.Supervisor?.Staff?.FullName ?? "Unassigned",
                TotalHours = timesheet.TotalHours,
                RegularHours = timesheet.TotalRegularHours,
                OvertimeHours = timesheet.TotalOvertimeHours,
                DaysWorked = timesheet.DaysWorked,
                ExceptionCount = timesheet.ExceptionCount,
                EmployeeApproved = timesheet.EmployeeApprovalStatus != ApprovalStatus.Pending,
                SupervisorApproved = timesheet.SupervisorApprovalStatus == ApprovalStatus.Approved,
                HRApproved = timesheet.HRApprovalStatus == ApprovalStatus.Approved,
                SupervisorApprovedBy = timesheet.SupervisorApprovedBy,
                SupervisorApprovedDate = timesheet.SupervisorApprovedDate,
                HRApprovedBy = timesheet.HRApprovedBy,
                HRApprovedDate = timesheet.HRApprovedDate
            });
        }

        return summaries.OrderBy(s => s.SupervisorName).ThenBy(s => s.EmployeeName).ToList();
    }

    public async Task RecalculateDailyTimecardAsync(int employeeId, DateOnly workDate)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var punches = await context.TcTimePunches
            .Where(p => p.EmployeeId == employeeId && 
                        DateOnly.FromDateTime(p.PunchDateTime) == workDate &&
                        p.PunchStatus == PunchStatus.Active)
            .OrderBy(p => p.PunchDateTime)
            .ToListAsync();

        var totalHours = CalculateDayHours(punches);

        var timecard = await context.TcDailyTimecards
            .FirstOrDefaultAsync(t => t.EmployeeId == employeeId && t.WorkDate == workDate);

        if (timecard == null)
        {
            var employee = await context.TcEmployees.FindAsync(employeeId);
            timecard = new TcDailyTimecard
            {
                EmployeeId = employeeId,
                CampusId = employee?.HomeCampusId ?? 1,
                WorkDate = workDate,
                CreatedDate = DateTime.Now
            };
            context.TcDailyTimecards.Add(timecard);
        }

        timecard.FirstPunchIn = punches.FirstOrDefault(p => p.PunchType == PunchType.In)?.PunchDateTime;
        timecard.LastPunchOut = punches.LastOrDefault(p => p.PunchType == PunchType.Out)?.PunchDateTime;
        timecard.TotalHours = totalHours;
        timecard.RegularHours = totalHours; // Overtime calculated at week level
        timecard.OvertimeHours = 0;
        
        // Exception detection
        timecard.IsMissedPunch = DetectMissedPunch(punches);
        timecard.HasException = timecard.IsMissedPunch || totalHours > 12;
        
        if (timecard.HasException)
        {
            var exceptions = new List<string>();
            if (timecard.IsMissedPunch) exceptions.Add("Missed punch");
            if (totalHours > 12) exceptions.Add($"Long shift ({totalHours:F1}h)");
            timecard.ExceptionNotes = string.Join("; ", exceptions);
        }

        timecard.ModifiedDate = DateTime.Now;
        await context.SaveChangesAsync();

        // Audit: TIMECARD_RECALCULATED. This is an admin-initiated operation from
        // TeamTimesheets or HRPayroll, so AdminUi source. Includes the final totals
        // so compliance reports can see what was adjusted.
        await _audit.LogActionAsync(
            actionCode: AuditActions.Timecard.Recalculated,
            entityType: AuditEntityTypes.Timecard,
            entityId: timecard.TimecardId.ToString(),
            newValues: new
            {
                timecard.TimecardId,
                timecard.EmployeeId,
                timecard.WorkDate,
                timecard.TotalHours,
                timecard.RegularHours,
                timecard.OvertimeHours,
                timecard.HasException,
                timecard.ExceptionNotes,
                PunchCount = punches.Count
            },
            deltaSummary: $"Recalculated timecard for {workDate:yyyy-MM-dd}: {punches.Count} punches → {totalHours:F2}h{(timecard.HasException ? " (exception)" : "")}",
            source: AuditSource.AdminUi,
            employeeId: employeeId,
            campusId: timecard.CampusId);
    }

    public async Task SetShortDayReasonAsync(int employeeId, DateOnly workDate, string? reason, string? note, string modifiedBy)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        var normalizedNote   = string.IsNullOrWhiteSpace(note)   ? null : note.Trim();

        var timecard = await context.TcDailyTimecards
            .FirstOrDefaultAsync(t => t.EmployeeId == employeeId && t.WorkDate == workDate);

        if (timecard == null)
        {
            // Employee is annotating a day with no punches yet (e.g., Weather
            // Closure day). Create a zero-hours card so the reason sticks and
            // the row renders on the timesheet view.
            var employee = await context.TcEmployees.FindAsync(employeeId);
            timecard = new TcDailyTimecard
            {
                EmployeeId = employeeId,
                CampusId = employee?.HomeCampusId ?? 1,
                WorkDate = workDate,
                CreatedDate = DateTime.Now
            };
            context.TcDailyTimecards.Add(timecard);
        }

        var oldReason = timecard.ShortDayReason;
        var oldNote   = timecard.ShortDayNote;

        timecard.ShortDayReason = normalizedReason;
        timecard.ShortDayNote   = normalizedNote;
        timecard.ModifiedDate   = DateTime.Now;

        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.Timecard.ExceptionFlagged,
            entityType: AuditEntityTypes.Timecard,
            entityId: timecard.TimecardId.ToString(),
            oldValues: new { ShortDayReason = oldReason, ShortDayNote = oldNote },
            newValues: new { timecard.ShortDayReason, timecard.ShortDayNote, ModifiedBy = modifiedBy },
            deltaSummary: $"ShortDayReason={normalizedReason ?? "(cleared)"} for {workDate:yyyy-MM-dd}"
                        + (string.IsNullOrEmpty(normalizedNote) ? "" : $" note=\"{normalizedNote}\""),
            source: AuditSource.AdminUi,
            employeeId: employeeId,
            campusId: timecard.CampusId);
    }

    public async Task RecalculateWeeklyOvertimeAsync(int employeeId, DateOnly weekStartDate)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        while (weekStartDate.DayOfWeek != DayOfWeek.Monday)
            weekStartDate = weekStartDate.AddDays(-1);

        var weekEndDate = weekStartDate.AddDays(6);

        var dailyCards = await context.TcDailyTimecards
            .Where(t => t.EmployeeId == employeeId && t.WorkDate >= weekStartDate && t.WorkDate <= weekEndDate)
            .OrderBy(t => t.WorkDate)
            .ToListAsync();

        var totalWeekHours = dailyCards.Sum(d => d.TotalHours);
        var overtimeHours = Math.Max(0, totalWeekHours - OVERTIME_THRESHOLD);
        var regularHours = Math.Min(totalWeekHours, OVERTIME_THRESHOLD);

        // Distribute overtime to last days worked
        if (overtimeHours > 0)
        {
            var remainingOT = overtimeHours;
            foreach (var card in dailyCards.OrderByDescending(d => d.WorkDate))
            {
                if (remainingOT <= 0) break;

                var cardOT = Math.Min(card.TotalHours, remainingOT);
                card.OvertimeHours = cardOT;
                card.RegularHours = card.TotalHours - cardOT;
                remainingOT -= cardOT;
            }
        }

        await context.SaveChangesAsync();

        // Audit: TIMECARD_OVERTIME_REDISTRIBUTED only fires when overtime actually exists.
        // Calls with zero OT are no-ops from an audit perspective — they don't change
        // distribution, just confirm the status, and we don't need to log that.
        if (overtimeHours > 0)
        {
            await _audit.LogActionAsync(
                actionCode: AuditActions.Timecard.OvertimeRedistributed,
                entityType: AuditEntityTypes.Timecard,
                entityId: $"{employeeId}:week:{weekStartDate:yyyy-MM-dd}",
                newValues: new
                {
                    EmployeeId = employeeId,
                    WeekStart = weekStartDate,
                    WeekEnd = weekEndDate,
                    TotalWeekHours = totalWeekHours,
                    RegularHours = regularHours,
                    OvertimeHours = overtimeHours,
                    CardCount = dailyCards.Count
                },
                deltaSummary: $"Redistributed {overtimeHours:F2}h OT across week {weekStartDate:yyyy-MM-dd} (total {totalWeekHours:F2}h worked)",
                source: AuditSource.AdminUi,
                employeeId: employeeId);
        }
    }

    private static decimal CalculateDayHours(List<TcTimePunch> punches)
    {
        if (!punches.Any()) return 0;

        decimal totalMinutes = 0;
        TcTimePunch? lastIn = null;

        foreach (var punch in punches.OrderBy(p => p.PunchDateTime))
        {
            if (punch.PunchType == PunchType.In || punch.PunchType == PunchType.LunchIn || punch.PunchType == PunchType.BreakIn)
            {
                lastIn = punch;
            }
            else if (lastIn != null)
            {
                totalMinutes += (decimal)(punch.PunchDateTime - lastIn.PunchDateTime).TotalMinutes;
                lastIn = null;
            }
        }

        // If still clocked in, fill the missing OUT with "now" — ONLY when the
        // orphan IN is from today. For historical unpaired INs (which happen
        // after a bulk void cleanup or broken backfill pairings), filling
        // with Now produces runaway hours like 168, 340, 510 because Now
        // is days or weeks past the IN timestamp. Prior to 2026-04-23 this
        // code caused Jasmine's 4/02 card to show 510.37 hours during recalc.
        // Historical orphan INs contribute 0 minutes; DetectMissedPunch will
        // flag IsMissedPunch so the admin sees it and can fix via a
        // TcCorrectionRequest / manual edit.
        if (lastIn != null)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            var inDate = DateOnly.FromDateTime(lastIn.PunchDateTime);
            if (inDate == today)
            {
                totalMinutes += (decimal)(DateTime.Now - lastIn.PunchDateTime).TotalMinutes;
            }
            // else: historical unpaired IN — skip; missed-punch flag handles it.
        }

        return Math.Round(totalMinutes / 60, 2);
    }

    private static bool DetectMissedPunch(List<TcTimePunch> punches)
    {
        if (!punches.Any()) return false;

        // Check for unmatched punches
        int inCount = punches.Count(p => p.PunchType == PunchType.In || p.PunchType == PunchType.LunchIn || p.PunchType == PunchType.BreakIn);
        int outCount = punches.Count(p => p.PunchType == PunchType.Out || p.PunchType == PunchType.LunchOut || p.PunchType == PunchType.BreakOut);

        // If counts don't match and it's past end of day
        if (Math.Abs(inCount - outCount) > 0 && DateTime.Now.Hour >= 18)
            return true;

        return false;
    }
}

// Timesheet DTOs
public class WeeklyTimesheet
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public DateOnly WeekStartDate { get; set; }
    public DateOnly WeekEndDate { get; set; }
    public List<DailyTimesheetEntry> Days { get; set; } = new();
    public decimal TotalRegularHours { get; set; }
    public decimal TotalOvertimeHours { get; set; }
    public decimal TotalHours { get; set; }
    public bool HasExceptions { get; set; }
    public bool IsSubmitted { get; set; }
}

public class DailyTimesheetEntry
{
    public DateOnly Date { get; set; }
    public string DayOfWeek { get; set; } = "";
    public List<PunchEntry> Punches { get; set; } = new();
    public decimal TotalHours { get; set; }
    public decimal RegularHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public bool HasException { get; set; }
    public string? ExceptionNotes { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }

    /// <summary>Migration 052: short-day reason + note for employee annotation.</summary>
    public string? ShortDayReason { get; set; }
    public string? ShortDayNote { get; set; }

    /// <summary>
    /// Earliest IN punch of the day. Rendered on the collapsed row so users
    /// can see arrival time without expanding the punch-list drill-down.
    /// Derived on demand from <see cref="Punches"/> so callers that bypass
    /// the service layer still get a consistent result.
    /// </summary>
    public PunchEntry? FirstIn => Punches
        .Where(p => string.Equals(p.PunchType, "In", StringComparison.OrdinalIgnoreCase))
        .OrderBy(p => p.PunchTime)
        .FirstOrDefault();

    /// <summary>
    /// Latest OUT punch of the day. Rendered alongside <see cref="FirstIn"/>
    /// on the collapsed row. Null while the employee is still clocked in
    /// (no matching Out yet).
    /// </summary>
    public PunchEntry? LastOut => Punches
        .Where(p => string.Equals(p.PunchType, "Out", StringComparison.OrdinalIgnoreCase))
        .OrderByDescending(p => p.PunchTime)
        .FirstOrDefault();
}

public class PunchEntry
{
    public long PunchId { get; set; }
    public string PunchType { get; set; } = "";
    public DateTime PunchTime { get; set; }
    public DateTime? RoundedTime { get; set; }
    public bool IsManual { get; set; }
    public bool IsModified { get; set; }
}

public class PayPeriodTimesheet
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string? SupervisorName { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public List<WeeklyTimesheet> Weeks { get; set; } = new();
    public decimal TotalRegularHours { get; set; }
    public decimal TotalOvertimeHours { get; set; }
    public decimal TotalHours { get; set; }
    public int DaysWorked { get; set; }
    public int ExceptionCount { get; set; }
    public ApprovalStatus EmployeeApprovalStatus { get; set; }
    public DateTime? EmployeeApprovedDate { get; set; }
    public ApprovalStatus SupervisorApprovalStatus { get; set; }
    public string? SupervisorApprovedBy { get; set; }
    public DateTime? SupervisorApprovedDate { get; set; }
    public ApprovalStatus HRApprovalStatus { get; set; }
    public string? HRApprovedBy { get; set; }
    public DateTime? HRApprovedDate { get; set; }
}

public class TeamTimesheetSummary
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string IdNumber { get; set; } = "";
    public decimal TotalHours { get; set; }
    public decimal RegularHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public int DaysWorked { get; set; }
    public int ExceptionCount { get; set; }
    public bool EmployeeApproved { get; set; }
    public bool SupervisorApproved { get; set; }
    public bool HasExceptions { get; set; }

    /// <summary>Migration 052: distinct ShortDayReason codes across the pay period.</summary>
    public List<string> ShortDayReasons { get; set; } = new();

    // Admin-view context. Populated by both GetTeamTimesheetsAsync (regular
    // supervisor scope) and GetAllTeamTimesheetsAsync (admin scope) so the
    // UI can render one consistent grid and filter visually.
    public string? SupervisorName { get; set; }
    public int? SupervisorEmployeeId { get; set; }
    public string? CampusName { get; set; }
    public int? CampusId { get; set; }
}

public class PayrollSummary
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string IdNumber { get; set; } = "";
    public string? AscenderEmployeeId { get; set; }
    public string SupervisorName { get; set; } = "";
    public decimal TotalHours { get; set; }
    public decimal RegularHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public int DaysWorked { get; set; }
    public int ExceptionCount { get; set; }
    public bool EmployeeApproved { get; set; }
    public bool SupervisorApproved { get; set; }
    public bool HRApproved { get; set; }
    public string? SupervisorApprovedBy { get; set; }
    public DateTime? SupervisorApprovedDate { get; set; }
    public string? HRApprovedBy { get; set; }
    public DateTime? HRApprovedDate { get; set; }
}





