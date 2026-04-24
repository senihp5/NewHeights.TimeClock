using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Hosting;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
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
                var now = DateTime.Now;
                var targetTime = new DateTime(now.Year, now.Month, now.Day, AutoCheckoutHour, AutoCheckoutMinute, 0);

                // If we've passed today's checkout time, target tomorrow
                if (now > targetTime)
                {
                    targetTime = targetTime.AddDays(1);
                }

                var delay = targetTime - now;
                _logger.LogInformation("Next auto-checkout scheduled for {TargetTime} ({Delay} from now)",
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
        var audit = scope.ServiceProvider.GetRequiredService<IAuditService>();
        using var context = await dbFactory.CreateDbContextAsync();

        var now = DateTime.Now;
        var todayStart = now.Date;

        _logger.LogInformation("Processing auto-checkouts for {Date}", now.Date);

        // Find all employees with IN punch today but no OUT punch
        var openPunches = await context.TcTimePunches
                    .Include(p => p.Employee)
                    .ThenInclude(e => e!.Staff)
                    .Where(p => p.PunchDateTime >= todayStart &&
                                p.PunchType == PunchType.In &&
                        p.PunchStatus == PunchStatus.Active &&
                        p.PairedPunchId == null)
            .ToListAsync();

        _logger.LogInformation("Found {Count} open punches requiring auto-checkout", openPunches.Count);

        foreach (var inPunch in openPunches)
        {
            try
            {
                // Guard: if this employee already has an active OUT punch for today
                // (regardless of whether inPunch.PairedPunchId was updated), DON'T
                // create another auto-checkout. Repair the IN-OUT linkage instead
                // so the query won't pick up this IN again tomorrow.
                //
                // Root cause for the bogus 9:30 data we cleaned 2026-04-22: the
                // regular check-out flow occasionally failed to set IN.PairedPunchId
                // after creating the OUT. AutoCheckout saw the unpaired IN and
                // stacked a redundant OUT at 9:30. This guard defends against that.
                var existingOut = await context.TcTimePunches
                    .Where(o => o.EmployeeId == inPunch.EmployeeId
                             && o.PunchType == PunchType.Out
                             && o.PunchStatus == PunchStatus.Active
                             && o.PunchDateTime >= todayStart
                             && o.PunchId != inPunch.PunchId)
                    .OrderByDescending(o => o.PunchDateTime)
                    .FirstOrDefaultAsync();

                if (existingOut != null)
                {
                    _logger.LogWarning(
                        "AutoCheckout: skipping EmployeeId {Id} (IN punch #{InId}) — active OUT punch #{OutId} at {OutTime} already exists for today. Repairing pairing.",
                        inPunch.EmployeeId, inPunch.PunchId, existingOut.PunchId, existingOut.PunchDateTime);

                    // Repair the broken linkage so future runs of this loop skip
                    // this IN via the PairedPunchId == null filter.
                    inPunch.PairedPunchId = existingOut.PunchId;
                    if (existingOut.PairedPunchId == null)
                        existingOut.PairedPunchId = inPunch.PunchId;
                    await context.SaveChangesAsync();

                    continue;
                }

                var autoCheckoutTime = new DateTime(now.Year, now.Month, now.Day, AutoCheckoutHour, AutoCheckoutMinute, 0);

                var outPunch = new TcTimePunch
                {
                    EmployeeId = inPunch.EmployeeId,
                    CampusId = inPunch.CampusId,
                    TerminalId = null,
                    PunchType = PunchType.Out,
                    PunchDateTime = autoCheckoutTime,
                    RoundedDateTime = autoCheckoutTime,
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
                    CreatedDate = now
                };

                context.TcTimePunches.Add(outPunch);
                await context.SaveChangesAsync();

                // Update the IN punch to link to OUT punch
                inPunch.PairedPunchId = outPunch.PunchId;
                await context.SaveChangesAsync();

                var empName = inPunch.Employee?.Staff?.FullName ?? $"EmployeeID {inPunch.EmployeeId}";
                _logger.LogInformation("Auto-checkout created for {Employee} - IN:{InPunchId} OUT:{OutPunchId}",
                    empName, inPunch.PunchId, outPunch.PunchId);

                // Audit: one AUTO_CHECKOUT row per auto-generated OUT punch.
                // Source=SYSTEM because there is no authenticated user in the BackgroundService loop.
                await audit.LogActionAsync(
                    actionCode: AuditActions.AutoCheckout.Created,
                    entityType: AuditEntityTypes.Punch,
                    entityId: outPunch.PunchId.ToString(),
                    newValues: new
                    {
                        outPunch.PunchId,
                        outPunch.EmployeeId,
                        outPunch.CampusId,
                        outPunch.PunchDateTime,
                        PairedInPunchId = inPunch.PunchId,
                        outPunch.SessionType
                    },
                    deltaSummary: $"Auto-checkout at {autoCheckoutTime:HH:mm} paired with IN punch #{inPunch.PunchId} for {empName}",
                    source: AuditSource.System,
                    employeeId: inPunch.EmployeeId,
                    campusId: inPunch.CampusId,
                    punchId: outPunch.PunchId);

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