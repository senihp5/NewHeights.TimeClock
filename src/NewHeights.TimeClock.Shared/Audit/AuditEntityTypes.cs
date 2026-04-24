namespace NewHeights.TimeClock.Shared.Audit;

/// <summary>
/// Constants for the EntityType column on TC_AuditLog. One row per thing we
/// write about. Column is NVARCHAR(30) — keep values short and uppercase.
/// </summary>
public static class AuditEntityTypes
{
    public const string Punch              = "PUNCH";
    public const string Timecard           = "TIMECARD";
    public const string PaySummary         = "PAY_SUMMARY";
    public const string PayPeriod          = "PAY_PERIOD";
    public const string Correction         = "CORRECTION";
    public const string Employee           = "EMPLOYEE";
    public const string Holiday            = "HOLIDAY";
    public const string HoursWindow        = "HOURS_WINDOW";
    public const string MasterSchedule     = "MASTER_SCHEDULE";
    public const string Config             = "CONFIG";
    public const string System             = "SYSTEM";
    public const string SubRequest         = "SUB_REQUEST";
    public const string SubOutreach        = "SUB_OUTREACH";
    public const string SubTimecard        = "SUB_TIMECARD";
    public const string SubPeriodEntry     = "SUB_PERIOD_ENTRY";
    public const string AttendanceTx       = "ATTENDANCE_TX";
    public const string BellSchedule       = "BELL_SCHEDULE";
    public const string BellPeriod         = "BELL_PERIOD";
}
