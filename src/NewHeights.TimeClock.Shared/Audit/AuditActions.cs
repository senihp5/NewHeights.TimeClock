namespace NewHeights.TimeClock.Shared.Audit;

/// <summary>
/// Full catalog of 65 action codes across 16 process areas (see spec section 19.1).
/// Grouped into nested static classes by process area. Column is NVARCHAR(30).
/// Constants guarantee typos fail at compile time instead of polluting the audit log.
/// </summary>
public static class AuditActions
{
    // A. Time Punches
    public static class Punch
    {
        public const string Created         = "PUNCH_CREATED";
        public const string ManualEntry     = "PUNCH_MANUAL_ENTRY";
        public const string Modified        = "PUNCH_MODIFIED";
        public const string Voided          = "PUNCH_VOIDED";
        public const string Paired          = "PUNCH_PAIRED";
        public const string EarlyOutFlag    = "PUNCH_EARLY_OUT_FLAG";
        public const string GeofenceFail    = "PUNCH_GEOFENCE_FAIL";
    }

    // B. Auto-Checkout
    public static class AutoCheckout
    {
        public const string Created         = "AUTO_CHECKOUT";
    }

    // C. Daily Timecards
    public static class Timecard
    {
        public const string Created                 = "TIMECARD_CREATED";
        public const string Recalculated            = "TIMECARD_RECALCULATED";
        public const string OvertimeRedistributed   = "TIMECARD_OVERTIME_REDISTRIBUTED";
        public const string ExceptionFlagged        = "TIMECARD_EXCEPTION_FLAGGED";
    }

    // D/E. Employee + Supervisor Timesheets
    public static class Timesheet
    {
        public const string Submitted               = "TIMESHEET_SUBMITTED";
        public const string SupervisorApproved      = "TIMESHEET_SUPERVISOR_APPROVED";
        public const string SupervisorRejected      = "TIMESHEET_SUPERVISOR_REJECTED";
        public const string ExceptionAcknowledged   = "TIMESHEET_EXCEPTION_ACK";
        public const string ExceptionFlagged        = "TIMESHEET_EXCEPTION_FLAGGED";
    }

    // F. HR Payroll
    public static class Payroll
    {
        public const string HRApproved      = "PAYROLL_HR_APPROVED";
        public const string HRRejected      = "PAYROLL_HR_REJECTED";
        public const string Exported        = "PAYROLL_EXPORTED";
        public const string PeriodUnlocked  = "PAYROLL_PERIOD_UNLOCKED";
    }

    // G. Pay Periods
    public static class PayPeriod
    {
        public const string Uploaded   = "PAY_PERIOD_UPLOADED";
        public const string Deleted    = "PAY_PERIOD_DELETED";
        public const string Modified   = "PAY_PERIOD_MODIFIED";
    }

    // H. Employee Sync
    public static class EmployeeSync
    {
        public const string SyncStarted     = "EMPLOYEE_SYNC_STARTED";
        public const string SyncCompleted   = "EMPLOYEE_SYNC_COMPLETED";
        public const string Created         = "EMPLOYEE_CREATED";
        public const string Updated         = "EMPLOYEE_UPDATED";
        public const string Deactivated     = "EMPLOYEE_DEACTIVATED";
        public const string Reactivated     = "EMPLOYEE_REACTIVATED";
        public const string SupervisorLinked = "EMPLOYEE_SUPERVISOR_LINKED";
        public const string StaffMatched    = "EMPLOYEE_STAFF_MATCHED";
    }

    // I. Holiday Schedule
    public static class Holiday
    {
        public const string Created      = "HOLIDAY_CREATED";
        public const string Modified     = "HOLIDAY_MODIFIED";
        public const string Deleted      = "HOLIDAY_DELETED";
        public const string PdfImported  = "HOLIDAY_PDF_IMPORTED";
    }

    // J. Staff Hours Windows
    public static class HoursWindow
    {
        public const string Created      = "HOURS_WINDOW_CREATED";
        public const string Modified     = "HOURS_WINDOW_MODIFIED";
        public const string Deleted      = "HOURS_WINDOW_DELETED";
    }

    // K. Master Schedule
    public static class Schedule
    {
        public const string Imported            = "SCHEDULE_IMPORTED";
        public const string PriorDeactivated    = "SCHEDULE_PRIOR_DEACTIVATED";
        public const string EntryModified       = "SCHEDULE_ENTRY_MODIFIED";
        public const string TeacherMatched      = "SCHEDULE_TEACHER_MATCHED";
    }

    // L. Correction Requests (future)
    public static class Correction
    {
        public const string Submitted  = "CORRECTION_SUBMITTED";
        public const string Approved   = "CORRECTION_APPROVED";
        public const string Denied     = "CORRECTION_DENIED";
        public const string Applied    = "CORRECTION_APPLIED";
    }

    // M. Substitute — Absence Requests
    public static class Absence
    {
        public const string Submitted  = "ABSENCE_SUBMITTED";
        public const string Approved   = "ABSENCE_APPROVED";
        public const string Denied     = "ABSENCE_DENIED";
        public const string Cancelled  = "ABSENCE_CANCELLED";
        public const string Modified   = "ABSENCE_MODIFIED";
        public const string Escalated  = "ABSENCE_ESCALATED";
    }

    // N. Substitute — Outreach & Scheduling
    public static class SubOutreach
    {
        public const string SmsSent             = "SUB_SMS_SENT";
        public const string EmailSent           = "SUB_EMAIL_SENT";
        public const string SmsDelivered        = "SUB_SMS_DELIVERED";
        public const string SmsFailed           = "SUB_SMS_FAILED";
        public const string Accepted            = "SUB_ACCEPTED";
        public const string Declined            = "SUB_DECLINED";
        public const string TokenExpired        = "SUB_TOKEN_EXPIRED";
        public const string ConfirmationSent    = "SUB_CONFIRMATION_SENT";
        public const string Reassigned          = "SUB_REASSIGNED";
    }

    // N2. Substitute — Pool Management (Phase D5)
    public static class SubPool
    {
        public const string Excluded            = "SUB_POOL_EXCLUDED";
        public const string Restored            = "SUB_POOL_RESTORED";
        public const string SpecialtyAssigned   = "SUB_SPECIALTY_ASSIGNED";
        public const string SpecialtyUnassigned = "SUB_SPECIALTY_UNASSIGNED";
        // D5 followup: supervisors can manually flip SubRole on a sub without
        // waiting for the Entra overlay. Distinct codes so audit reports can
        // tell manual overrides apart from the overlay-driven SUBRole flips
        // that happen silently inside ApplyAdminSubOverlayAsync.
        public const string RoleManualSet       = "SUB_ROLE_MANUAL_SET";
        public const string RoleManualUnset     = "SUB_ROLE_MANUAL_UNSET";
    }

    // O. Substitute — Timecards
    public static class SubTimecard
    {
        public const string Created         = "SUB_TIMECARD_CREATED";
        public const string PeriodAdded     = "SUB_PERIOD_ADDED";
        public const string PeriodRemoved   = "SUB_PERIOD_REMOVED";
        public const string PeriodModified  = "SUB_PERIOD_MODIFIED";
        public const string Approved        = "SUB_TIMECARD_APPROVED";
        public const string Rejected        = "SUB_TIMECARD_REJECTED";
        public const string Unlocked        = "SUB_TIMECARD_UNLOCKED";
        public const string PeriodVerified  = "SUB_PERIOD_VERIFIED";
        public const string PayrollApproved = "SUB_PAYROLL_APPROVED";
        public const string PayrollExported = "SUB_PAYROLL_EXPORTED";
    }

    // P. System Configuration
    public static class Config
    {
        public const string Changed = "CONFIG_CHANGED";
    }

    // Q. Attendance Transactions (reception manual override / geofence tightening)
    public static class Attendance
    {
        public const string ManualOverride = "ATTENDANCE_MANUAL_OVERRIDE";
    }
}
