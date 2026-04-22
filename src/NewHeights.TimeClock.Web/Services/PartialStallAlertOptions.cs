namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase A: bindable config for PartialStallAlertService. Bound from
/// appsettings.json section "PartialStallAlert" in Program.cs.
///
/// Each tick walks active TcSubRequests where Status=PartiallyAssigned and
/// ModifiedDate is older than ThresholdHours, and the request hasn't yet
/// been alerted (PartialStallAlertSentAt IS NULL). For each qualifying
/// request, emails + SMS the requesting employee's supervisor with the
/// remaining uncovered periods.
///
/// Dedup: once alerted, PartialStallAlertSentAt is stamped so the same
/// stall-window can't be alerted again. ProcessAcceptAsync resets the
/// column back to NULL on any new partial accept — a fresh accept is
/// interpreted as "progress has been made, restart the stall clock."
/// </summary>
public class PartialStallAlertOptions
{
    /// <summary>Master switch. False = service starts but RunOnceAsync returns
    /// immediately. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the service wakes up to scan. Default 60 minutes —
    /// the threshold is 24 hours so hourly granularity is plenty.</summary>
    public int ScanIntervalMinutes { get; set; } = 60;

    /// <summary>Delay after app startup before the first scan. Default 10
    /// minutes so the app warms up before the first alert fan-out.</summary>
    public int InitialDelayMinutes { get; set; } = 10;

    /// <summary>
    /// Hours a request must have been stuck in PartiallyAssigned before the
    /// supervisor is nudged. Default 24. Min-clamp 1 so a misconfigured 0
    /// doesn't generate an alert the moment a partial accept lands.
    /// </summary>
    public int ThresholdHours { get; set; } = 24;
}
