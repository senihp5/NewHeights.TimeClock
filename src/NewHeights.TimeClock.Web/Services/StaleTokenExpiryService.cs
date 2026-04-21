using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase 7a background job. Periodically walks TcSubOutreach for rows whose
/// token has expired without a response and marks them EXPIRED. When a row
/// expires inside an auto-cascade queue, ISubOutreachService also advances to
/// the next queued sub (sending email/SMS to them + auditing SUB_REASSIGNED).
///
/// Phase D2 (2026-04-21): tick interval and initial delay are now bound from
/// SubOutreachOptions. Defaults: 15-minute scan cadence, 5-minute initial
/// delay. Must be tighter than SubOutreachOptions.TokenValidityHours or
/// cascade advancement is bottlenecked by the scan cadence instead of the
/// token lifetime.
///
/// Pattern copied from AutoCheckoutService: BackgroundService with a single-
/// ExecuteAsync loop, uses IServiceScopeFactory to resolve the scoped
/// ISubOutreachService on each tick, Task.Delay between runs.
/// </summary>
public class StaleTokenExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SubOutreachOptions _options;
    private readonly ILogger<StaleTokenExpiryService> _logger;

    public StaleTokenExpiryService(
        IServiceScopeFactory scopeFactory,
        IOptions<SubOutreachOptions> options,
        ILogger<StaleTokenExpiryService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Clamp to sane minimums: 1 minute floor on scan cadence, 1 minute
        // floor on initial delay. Protects against a config typo setting 0.
        var initialDelay = TimeSpan.FromMinutes(Math.Max(1, _options.InitialDelayMinutes));
        var runInterval  = TimeSpan.FromMinutes(Math.Max(1, _options.ScanIntervalMinutes));

        _logger.LogInformation(
            "StaleTokenExpiryService started. Initial delay {InitialDelay}, scan interval {Interval}, token validity {Validity}h.",
            initialDelay, runInterval, _options.TokenValidityHours);

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
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "StaleTokenExpiryService: tick threw — will retry after interval.");
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

        _logger.LogInformation("StaleTokenExpiryService stopping.");
    }

    private async Task RunOnceAsync(CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var outreach = scope.ServiceProvider.GetRequiredService<ISubOutreachService>();

        var started = DateTime.Now;
        var expiredCount = await outreach.ExpireStaleTokensAsync();

        if (expiredCount > 0)
        {
            _logger.LogInformation(
                "StaleTokenExpiryService: expired {Count} outreach token(s) in {Elapsed}. Auto-cascade advancement audited separately.",
                expiredCount, DateTime.Now - started);
        }
        else
        {
            _logger.LogDebug(
                "StaleTokenExpiryService: no expired tokens this run ({Elapsed}).",
                DateTime.Now - started);
        }
    }
}
