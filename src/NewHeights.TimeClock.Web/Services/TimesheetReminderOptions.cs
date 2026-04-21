namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Phase D3: bindable config for TimesheetReminderService. Bound from
/// appsettings.json section "TimesheetReminder" in Program.cs.
///
/// Each tick:
///   - For employees with a Pending TcPayPeriodSummary on the current period
///     (i.e. they haven't submitted yet), fire EMPLOYEE_48H if hours-to-deadline
///     is in (24, 48] and EMPLOYEE_24H if hours-to-deadline is in (0, 24].
///   - For supervisors with at least one direct-report Pending summary, fire
///     SUPERVISOR_DEADLINE on/after the deadline.
///   - Dedup via TC_TimesheetReminderLog unique index on
///     (PayPeriodId, EmployeeId, ReminderType).
/// </summary>
public class TimesheetReminderOptions
{
    /// <summary>Master switch. False = service starts but RunOnceAsync returns
    /// immediately. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the service wakes up to scan. Default 60 minutes —
    /// the dedup table makes per-tick cost cheap, and an hourly cadence is
    /// granular enough to catch the 24h/48h windows reliably.</summary>
    public int ScanIntervalMinutes { get; set; } = 60;

    /// <summary>Delay after app startup before the first scan. Default 10
    /// minutes so the app warms up and we don't blast a queue of overdue
    /// reminders during a routine restart.</summary>
    public int InitialDelayMinutes { get; set; } = 10;
}
