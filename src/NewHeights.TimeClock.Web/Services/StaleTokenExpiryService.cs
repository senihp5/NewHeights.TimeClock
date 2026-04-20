using Microsoft.Extensions.Hosting;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase 7a background job. Every 4 hours, walks TcSubOutreach for rows whose
/// token has expired without a response and marks them EXPIRED. When a row
/// expires inside an auto-cascade queue, ISubOutreachService also advances to
/// the next queued sub (sending email/SMS to them + auditing SUB_REASSIGNED).
///
/// Pattern copied from AutoCheckoutService: BackgroundService with a single-
/// ExecuteAsync loop, uses IServiceScopeFactory to resolve the scoped
/// ISubOutreachService on each tick, Task.Delay between runs.
/// </summary>
public class StaleTokenExpiryService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StaleTokenExpiryService> _logger;

    // Fire every 4 hours. Tight enough that auto-cascade advances promptly when
    // a token expires, loose enough to avoid DB chatter for the ~13-sub roster.
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(4);

    // First run 5 minutes after startup, not immediately — lets the app warm up.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);

    public StaleTokenExpiryService(
        IServiceScopeFactory scopeFactory,
        ILogger<StaleTokenExpiryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "StaleTokenExpiryService started. Initial delay {InitialDelay}, thereafter every {Interval}.",
            InitialDelay, RunInterval);

        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
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
                await Task.Delay(RunInterval, stoppingToken);
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
