namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase 9c: bindable config for SubRequestEscalationService. Bound from
/// appsettings.json section "SubRequestEscalation" in Program.cs.
///
/// Flow:
///   Every ScanIntervalHours the service wakes up and finds AwaitingSub
///   requests where (now - CreatedDate) > StaleThresholdHours AND either
///   EscalatedAt is NULL, or (now - EscalatedAt) > ReEscalationIntervalHours.
///   For each match, the service emails + (eventually) SMS-notifies the
///   teacher's supervisor, stamps EscalatedAt, and audits ABSENCE_ESCALATED.
/// </summary>
public class SubRequestEscalationOptions
{
    /// <summary>Master switch. If false, the background service still runs
    /// but the RunOnceAsync loop returns immediately without scanning.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long an AwaitingSub request must sit before the first
    /// escalation fires. Default 24h.</summary>
    public int StaleThresholdHours { get; set; } = 24;

    /// <summary>After the first escalation, how long before a repeat
    /// escalation email/SMS is allowed. Default 24h.</summary>
    public int ReEscalationIntervalHours { get; set; } = 24;

    /// <summary>BackgroundService tick interval. Default 4h (matches
    /// StaleTokenExpiryService).</summary>
    public int ScanIntervalHours { get; set; } = 4;

    /// <summary>Delay after app startup before the first scan. Default 5min
    /// so the app has time to warm up.</summary>
    public int InitialDelayMinutes { get; set; } = 5;
}
