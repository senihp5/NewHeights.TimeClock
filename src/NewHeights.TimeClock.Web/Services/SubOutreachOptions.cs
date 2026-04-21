namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase D2: bindable config for SubOutreachService + StaleTokenExpiryService.
/// Bound from appsettings.json section "SubOutreach" in Program.cs.
///
/// Replaces the hardcoded TokenValidityHours = 48 const and the
/// StaleTokenExpiryService RunInterval = 4h literal so the cascade cadence
/// can be tuned without a redeploy.
///
/// Defaults encode the 2-hour email cascade requested on 2026-04-21:
///   - Each sub has 2 hours to accept the emailed link before the token
///     expires and the cascade advances to the next sub in the queue.
///   - The background expiry scanner runs every 15 minutes so cascade
///     advancement is never bottlenecked by the scan cadence.
/// </summary>
public class SubOutreachOptions
{
    /// <summary>
    /// How long an outreach token stays valid after it's sent. When the sub
    /// doesn't accept/decline within this window, the scanner expires the
    /// token and (in auto-cascade mode) advances to the next queued sub.
    /// Default 2 hours.
    /// </summary>
    public int TokenValidityHours { get; set; } = 2;

    /// <summary>
    /// How often StaleTokenExpiryService wakes up to scan for expired
    /// tokens. Must be smaller than TokenValidityHours (in minutes) or
    /// cascade advancement is bottlenecked by this value instead of the
    /// token lifetime. Default 15 minutes.
    /// </summary>
    public int ScanIntervalMinutes { get; set; } = 15;

    /// <summary>
    /// Delay after app startup before the first scan. Default 5 minutes so
    /// the app has time to warm up.
    /// </summary>
    public int InitialDelayMinutes { get; set; } = 5;

    /// <summary>
    /// Phase A ext: token lifetime for Emergency Fill requests (TcSubRequest.
    /// IsEmergency=true). Default 30 minutes — aggressive enough to force a
    /// same-day decision but long enough a sub checking email mid-commute can
    /// still respond. Paired with the broadcast dispatch model (every
    /// candidate receives the request at once) so the first to accept wins.
    /// </summary>
    public int EmergencyTokenValidityMinutes { get; set; } = 30;
}
