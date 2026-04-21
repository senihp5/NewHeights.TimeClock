namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Phase D3 dedup row written by TimesheetReminderService each time it sends
/// a reminder email. Unique on (PayPeriodId, EmployeeId, ReminderType) so a
/// server bounce or scaler re-tick can't re-send the same reminder.
///
/// ReminderType values:
///   EMPLOYEE_48H        — 48h before EmployeeDeadline, hourly/PT/sub
///   EMPLOYEE_24H        — 24h before EmployeeDeadline, hourly/PT/sub
///   SUPERVISOR_DEADLINE — on/after EmployeeDeadline, supervisors with
///                         pending direct-report timesheets
/// </summary>
public class TcTimesheetReminderLog
{
    public long ReminderLogId { get; set; }
    public int PayPeriodId { get; set; }
    public int EmployeeId { get; set; }
    public string ReminderType { get; set; } = "";
    public DateTime SentAt { get; set; } = DateTime.Now;
    public string DeliveryStatus { get; set; } = "SENT";
    public string? ErrorMessage { get; set; }

    public TcPayPeriod PayPeriod { get; set; } = null!;
    public TcEmployee Employee { get; set; } = null!;
}
