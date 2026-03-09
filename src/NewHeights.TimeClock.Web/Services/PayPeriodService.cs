using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;

namespace NewHeights.TimeClock.Web.Services;

public interface IPayPeriodService
{
    Task<PayPeriodInfo> GetCurrentPayPeriodAsync();
    Task<PayPeriodInfo> GetPayPeriodForDateAsync(DateTime date);
    Task<List<PayPeriodInfo>> GetPayPeriodsForYearAsync(string schoolYear);
    Task<List<PayPeriodInfo>> GetAllUpcomingAsync(int count = 6);
    PayPeriodInfo GetCurrentPayPeriod();
    PayPeriodInfo GetPayPeriodForDate(DateTime date);
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
        var period = await context.TcPayPeriods
            .AsNoTracking()
            .Where(p => p.StartDate <= target && p.EndDate >= target)
            .OrderByDescending(p => p.StartDate)
            .FirstOrDefaultAsync();
        if (period != null) return MapToInfo(period);
        return ComputeFallback(date);
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

    public PayPeriodInfo GetCurrentPayPeriod() => GetPayPeriodForDate(DateTime.Today);
    public PayPeriodInfo GetPayPeriodForDate(DateTime date) => ComputeFallback(date);

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

    private static PayPeriodInfo ComputeFallback(DateTime date)
    {
        var day = date.Day;
        DateOnly startDate, endDate;
        if (day <= 15)
        {
            startDate = new DateOnly(date.Year, date.Month, 1);
            endDate = new DateOnly(date.Year, date.Month, 15);
        }
        else
        {
            startDate = new DateOnly(date.Year, date.Month, 16);
            endDate = new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
        }
        var payDate = AdjustForWeekend(endDate.Day == 15
            ? endDate
            : new DateOnly(endDate.Year, endDate.Month, 1).AddMonths(1));
        var deadline = payDate;
        while (deadline.DayOfWeek != DayOfWeek.Friday) deadline = deadline.AddDays(-1);
        if (payDate.DayOfWeek == DayOfWeek.Friday) deadline = deadline.AddDays(-7);
        return new PayPeriodInfo
        {
            StartDate = startDate, EndDate = endDate, PayDate = payDate,
            EmployeeDeadline = deadline,
            PeriodName = $"{startDate:MMM d} - {endDate:MMM d, yyyy} (est.)",
            SchoolYear = "", PeriodNumber = 0
        };
    }

    private static DateOnly AdjustForWeekend(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(-2),
        _ => date
    };
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
