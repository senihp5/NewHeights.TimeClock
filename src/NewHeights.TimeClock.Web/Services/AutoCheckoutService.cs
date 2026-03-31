using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

public interface IAutoCheckoutService
{
    Task ProcessAutoCheckoutsAsync();
}

public class AutoCheckoutService : BackgroundService, IAutoCheckoutService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AutoCheckoutService> _logger;
    private static readonly TimeZoneInfo CentralTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
    private const int AutoCheckoutHour = 21; // 9 PM
    private const int AutoCheckoutMinute = 30; // 30 minutes

    public AutoCheckoutService(
        IServiceScopeFactory scopeFactory,
        ILogger<AutoCheckoutService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("AutoCheckoutService started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var nowCst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, CentralTimeZone);
                var targetTime = new DateTime(nowCst.Year, nowCst.Month, nowCst.Day, AutoCheckoutHour, AutoCheckoutMinute, 0);

                // If we've passed today's checkout time, target tomorrow
                if (nowCst > targetTime)
                {
                    targetTime = targetTime.AddDays(1);
                }

                var delay = targetTime - nowCst;
                _logger.LogInformation("Next auto-checkout scheduled for {TargetTime} CST ({Delay} from now)", 
                    targetTime, delay);

                await Task.Delay(delay, stoppingToken);

                if (!stoppingToken.IsCancellationRequested)
                {
                    await ProcessAutoCheckoutsAsync();
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("AutoCheckoutService stopping");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AutoCheckoutService loop");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Retry after 5 min
            }
        }
    }

    public async Task ProcessAutoCheckoutsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TimeClockDbContext>>();
        using var context = await dbFactory.CreateDbContextAsync();

        var nowUtc = DateTime.UtcNow;
        var nowCst = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, CentralTimeZone);
        var todayStart = new DateTime(nowCst.Year, nowCst.Month, nowCst.Day, 0, 0, 0);
        var todayStartUtc = TimeZoneInfo.ConvertTimeToUtc(todayStart, CentralTimeZone);

        _logger.LogInformation("Processing auto-checkouts for {Date}", nowCst.Date);

        // Find all employees with IN punch today but no OUT punch
        var openPunches = await context.TcTimePunches
            .Include(p => p.Employee)
            .ThenInclude(e => e!.Staff)
            .Where(p => p.PunchDateTime >= todayStartUtc &&
                        p.PunchType == PunchType.In &&
                        p.PunchStatus == PunchStatus.Active &&
                        p.PairedPunchId == null)
            .ToListAsync();

        _logger.LogInformation("Found {Count} open punches requiring auto-checkout", openPunches.Count);

        foreach (var inPunch in openPunches)
        {
            try
            {
                var autoCheckoutTimeCst = new DateTime(nowCst.Year, nowCst.Month, nowCst.Day, AutoCheckoutHour, AutoCheckoutMinute, 0);
                var autoCheckoutTimeUtc = TimeZoneInfo.ConvertTimeToUtc(autoCheckoutTimeCst, CentralTimeZone);

                var outPunch = new TcTimePunch
                {
                    EmployeeId = inPunch.EmployeeId,
                    CampusId = inPunch.CampusId,
                    TerminalId = null,
                    PunchType = PunchType.Out,
                    PunchDateTime = autoCheckoutTimeUtc,
                    RoundedDateTime = autoCheckoutTimeUtc,
                    GeofenceStatus = GeofenceStatus.Manual,
                    VerificationMethod = "SYSTEM",
                    ScanMethod = "AUTO",
                    PunchStatus = PunchStatus.Active,
                    PairedPunchId = inPunch.PunchId,
                    IsManualEntry = false,
                    IsAutoCheckout = true,
                    Notes = $"Auto-checkout at {AutoCheckoutHour}:{AutoCheckoutMinute:D2} PM - Please verify with supervisor",
                    PunchSource = "SYSTEM",
                    SessionType = inPunch.SessionType,
                    CreatedDate = nowUtc
                };

                context.TcTimePunches.Add(outPunch);
                await context.SaveChangesAsync();

                // Update the IN punch to link to OUT punch
                inPunch.PairedPunchId = outPunch.PunchId;
                await context.SaveChangesAsync();

                var empName = inPunch.Employee?.Staff?.FullName ?? $"EmployeeID {inPunch.EmployeeId}";
                _logger.LogInformation("Auto-checkout created for {Employee} - IN:{InPunchId} OUT:{OutPunchId}", 
                    empName, inPunch.PunchId, outPunch.PunchId);

                // TODO: Send notification to employee and supervisor
                // await _emailService.SendAutoCheckoutNotificationAsync(inPunch.EmployeeId, autoCheckoutTimeCst);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating auto-checkout for PunchId {PunchId}", inPunch.PunchId);
            }
        }

        _logger.LogInformation("Auto-checkout processing complete - {Count} checkouts created", openPunches.Count);
    }
}
