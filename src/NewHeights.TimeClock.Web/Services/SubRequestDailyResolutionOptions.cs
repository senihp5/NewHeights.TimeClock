namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase C (2026-04-27): bindable config for SubRequestDailyResolutionService.
/// Bound from appsettings.json section "SubRequestDailyResolution" in Program.cs.
///
/// Each morning at RunHour (6 AM CST default), the service walks every
/// TcSubRequest whose StartDate equals today and whose status is not yet
/// finalized, and applies the appropriate resolution:
///
///   SubConfirmed       → AbsenceApproved  (auto-approve, notify both)
///   PartiallyAssigned  → no status change (notify supervisor with Take-Over link)
///   AwaitingSub /
///   Submitted /
///   SubAssigned        → Cancelled        (auto-cancel, notify both)
///
/// Emergency requests created less than EmergencyGraceHours before the run
/// are skipped — give a same-morning emergency the cascade some time to
/// land coverage instead of immediately auto-cancelling it.
/// </summary>
public class SubRequestDailyResolutionOptions
{
    /// <summary>Master switch. False = service starts but the daily sweep
    /// is a no-op. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Hour-of-day (0–23, local) when the sweep runs. Default 6 = 6 AM.
    /// The service tick frequency is independent of this; the wall-clock
    /// hour is what gates the actual sweep.</summary>
    public int RunHour { get; set; } = 6;

    /// <summary>How often the service wakes up to check whether it should
    /// run a sweep. Default 15 minutes — the dedup logic (LastRunDate
    /// in-memory + idempotent status transitions) makes occasional
    /// duplicate ticks safe.</summary>
    public int ScanIntervalMinutes { get; set; } = 15;

    /// <summary>Delay after app startup before the first scan. Default 10
    /// minutes so an early-morning restart doesn't immediately fire.</summary>
    public int InitialDelayMinutes { get; set; } = 10;

    /// <summary>Skip auto-cancel on emergency requests that were created
    /// less than this many hours before the run. Same-morning emergency
    /// requests should be allowed to keep their cascade in flight.
    /// Default 24 hours.</summary>
    public int EmergencyGraceHours { get; set; } = 24;

    /// <summary>Public origin for take-over deep links in the partial-day-of
    /// email. Falls back to clock.newheightsed.com if not set.</summary>
    public string PortalBaseUrl { get; set; } = "https://clock.newheightsed.com";
}
