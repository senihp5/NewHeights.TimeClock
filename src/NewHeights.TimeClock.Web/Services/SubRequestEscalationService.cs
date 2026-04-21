using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase 9c background job. Periodically scans TC_SubRequests for
/// AwaitingSub rows that have been sitting without outreach for longer
/// than SubRequestEscalationOptions.StaleThresholdHours, and delegates
/// per-request escalation to <see cref="ISubRequestEscalator"/>.
///
/// The per-request send / audit / stamp logic lives in
/// <see cref="SubRequestEscalator"/> so the manual "Escalate Now" button
/// on /supervisor/sub-requests uses the exact same code path.
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
                    _logger.LogDebug("SubRequestEscalationService: Enabled=false \u2014 skipping tick.");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SubRequestEscalationService: tick threw \u2014 will retry after interval.");
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
        var escalator = scope.ServiceProvider.GetRequiredService<ISubRequestEscalator>();

        var now          = DateTime.Now;
        var staleCutoff  = now.AddHours(-_options.StaleThresholdHours);
        var reEscCutoff  = now.AddHours(-_options.ReEscalationIntervalHours);

        await using var context = await dbFactory.CreateDbContextAsync(ct);

        // Just grab the IDs to minimize tracked state — the escalator loads
        // the full aggregate itself (supervisor + staff + campus includes).
        var staleIds = await context.TcSubRequests
            .Where(r => r.Status == SubRequestStatus.AwaitingSub
                     && r.CreatedDate < staleCutoff
                     && (r.EscalatedAt == null || r.EscalatedAt < reEscCutoff))
            .OrderBy(r => r.CreatedDate)
            .Select(r => r.SubRequestId)
            .ToListAsync(ct);

        if (staleIds.Count == 0)
        {
            _logger.LogDebug("SubRequestEscalationService: nothing stale this tick.");
            return;
        }

        _logger.LogInformation(
            "SubRequestEscalationService: {Count} AwaitingSub request(s) need escalation.",
            staleIds.Count);

        var escalated = 0;
        var skipped = 0;

        foreach (var id in staleIds)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await escalator.EscalateOneAsync(id, triggerSource: "background", ct: ct);
                if (result.Success) escalated++;
                else skipped++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SubRequestEscalationService: escalation threw for request {Id} \u2014 continuing with next.", id);
                skipped++;
            }
        }

        _logger.LogInformation(
            "SubRequestEscalationService: escalated {Escalated} request(s), skipped {Skipped}.",
            escalated, skipped);
    }
}
