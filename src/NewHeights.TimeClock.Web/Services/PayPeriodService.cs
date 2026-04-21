using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;

namespace NewHeights.TimeClock.Web.Services;

public interface IPayPeriodService
{
    // Async-only surface. Previous sync GetCurrentPayPeriod / GetPayPeriodForDate
    // methods were removed 2026-04-21 — they skipped the DB and returned a
    // semi-monthly computed fallback, masking imported biweekly pay periods
    // behind the wrong shape. All callers must await.
    Task<PayPeriodInfo> GetCurrentPayPeriodAsync();
    Task<PayPeriodInfo> GetPayPeriodForDateAsync(DateTime date);
    Task<List<PayPeriodInfo>> GetPayPeriodsForYearAsync(string schoolYear);
    Task<List<PayPeriodInfo>> GetAllUpcomingAsync(int count = 6);
}

public class PayPeriodService : IPayPeriodService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;

    public PayPeriodService(IDbContextFactory<TimeClockDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<PayPeriodInfo> GetCurrentPayPeriodAsync()
        => await GetPayPeriodForDateAsync(DateTime.Today);

    public async Task<PayPeriodInfo> GetPayPeriodForDateAsync(DateTime date)
    {
        var target = DateOnly.FromDateTime(date);
        using var context = await _dbFactory.CreateDbContextAsync();

        // Primary: a TcPayPeriod row that actually contains the target date.
        var period = await context.TcPayPeriods
            .AsNoTracking()
            .Where(p => p.StartDate <= target && p.EndDate >= target)
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync();
        if (period != null) return MapToInfo(period);

        // Fallback: project biweekly from the nearest anchor row in TcPayPeriods.
        // Only fires when the imported periods don't cover the target date (gap
        // in import, or target is outside the imported range). Matches the
        // organization's biweekly Mon–Sun cadence by inheriting it from the
        // existing data rather than guessing a semi-monthly shape.
        var anchor = await context.TcPayPeriods
            .AsNoTracking()
            .Where(p => p.StartDate <= target)
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync()
            ?? await context.TcPayPeriods
                .AsNoTracking()
                .OrderBy(p => p.StartDate)
                .FirstOrDefaultAsync();

        if (anchor != null) return ProjectBiweeklyFromAnchor(anchor, target);

        // Last resort: table is empty. Anchor to the most recent Monday so the
        // cadence is still biweekly Mon–Sun. Result is flagged (est.).
        return LastResortBiweekly(target);
    }

    public async Task<List<PayPeriodInfo>> GetPayPeriodsForYearAsync(string schoolYear)
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        var periods = await context.TcPayPeriods
            .AsNoTracking()
            .Where(p => p.SchoolYear == schoolYear)
            .OrderBy(p => p.StartDate)
            .ToListAsync();
        return periods.Select(MapToInfo).ToList();
    }

    public async Task<List<PayPeriodInfo>> GetAllUpcomingAsync(int count = 6)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        using var context = await _dbFactory.CreateDbContextAsync();
        var periods = await context.TcPayPeriods
            .AsNoTracking()
            .Where(p => p.EndDate >= today)
            .OrderBy(p => p.StartDate)
            .Take(count)
            .ToListAsync();
        return periods.Select(MapToInfo).ToList();
    }

    private static PayPeriodInfo MapToInfo(TcPayPeriod p) => new()
    {
        PayPeriodId = p.PayPeriodId,
        StartDate = p.StartDate,
        EndDate = p.EndDate,
        PayDate = p.PayDate ?? p.EndDate.AddDays(1),
        EmployeeDeadline = p.EmployeeDeadline ?? p.EndDate,
        PeriodName = p.PeriodName,
        SchoolYear = p.SchoolYear,
        PeriodNumber = p.PeriodNumber
    };

    // Project a biweekly (14-day) window from the given anchor so the returned
    // period covers the target date. PayDate + EmployeeDeadline offsets are
    // best-effort estimates derived from the typical biweekly cadence; the
    // result is marked (est.) via PayPeriodId = 0.
    private static PayPeriodInfo ProjectBiweeklyFromAnchor(TcPayPeriod anchor, DateOnly target)
    {
        int daysOffset = target.DayNumber - anchor.StartDate.DayNumber;
        int periodOffset = (int)Math.Floor(daysOffset / 14.0);
        var newStart = anchor.StartDate.AddDays(periodOffset * 14);
        var newEnd = newStart.AddDays(13);
        return new PayPeriodInfo
        {
            PayPeriodId = 0,
            StartDate = newStart,
            EndDate = newEnd,
            PayDate = newEnd.AddDays(19),
            EmployeeDeadline = newEnd.AddDays(2),
            PeriodName = $"{newStart:MMM d} - {newEnd:MMM d, yyyy} (est.)",
            SchoolYear = anchor.SchoolYear,
            PeriodNumber = 0
        };
    }

    private static PayPeriodInfo LastResortBiweekly(DateOnly target)
    {
        int daysSinceMonday = ((int)target.DayOfWeek + 6) % 7;
        var anchorMonday = target.AddDays(-daysSinceMonday);
        var newEnd = anchorMonday.AddDays(13);
        return new PayPeriodInfo
        {
            PayPeriodId = 0,
            StartDate = anchorMonday,
            EndDate = newEnd,
            PayDate = newEnd.AddDays(19),
            EmployeeDeadline = newEnd.AddDays(2),
            PeriodName = $"{anchorMonday:MMM d} - {newEnd:MMM d, yyyy} (est.)",
            SchoolYear = "",
            PeriodNumber = 0
        };
    }
}

public class PayPeriodInfo
{
    public int PayPeriodId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DateOnly PayDate { get; set; }
    public DateOnly EmployeeDeadline { get; set; }
    public string PeriodName { get; set; } = "";
    public string SchoolYear { get; set; } = "";
    public int PeriodNumber { get; set; }
    public bool IsOpen => DateOnly.FromDateTime(DateTime.Today) <= EndDate;
    public bool IsPastDeadline => DateOnly.FromDateTime(DateTime.Today) > EmployeeDeadline;
    public int DaysUntilDeadline => EmployeeDeadline.DayNumber - DateOnly.FromDateTime(DateTime.Today).DayNumber;
    public bool IsEstimated => PayPeriodId == 0;
}
