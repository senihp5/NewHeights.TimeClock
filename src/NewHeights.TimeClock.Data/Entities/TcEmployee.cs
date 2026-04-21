using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcEmployee
{
    public int EmployeeId { get; set; }
    public int? StaffDcid { get; set; }
    public required string IdNumber { get; set; }
    public string? DisplayName { get; set; }
    public string? AscenderEmployeeId { get; set; }
    public EmployeeType EmployeeType { get; set; } = EmployeeType.HourlyStaff;

    /// <summary>
    /// Which session(s) this employee is scheduled to work.
    /// Day = day session only (default).
    /// Evening = evening session only (evening teachers).
    /// Both = can work either session (substitutes).
    /// Used by ShouldPromptEarlyOut to match the correct TC_StaffHoursWindow.
    /// </summary>
    public EmployeeShift Shift { get; set; } = EmployeeShift.Day;

    public int HomeCampusId { get; set; }
    public int? SupervisorEmployeeId { get; set; }
    public string? DepartmentCode { get; set; }
    public string? DepartmentName { get; set; }
    public int? PayRuleId { get; set; }
    public DateOnly? HireDate { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public bool IsActive { get; set; } = true;
    public string? EntraObjectId { get; set; }

    /// <summary>
    /// When true, the outreach service will NOT send SMS to this employee
    /// (compliance for STOP / unsubscribe flows). Email is always allowed.
    /// Added by migration 035c for Phase 5/6.
    /// </summary>
    public bool SmsOptedOut { get; set; } = false;

    /// <summary>
    /// Migration 044 (Phase D4B): strict-isolation sub pool. Values:
    ///   TEACHER    — classroom / teacher coverage
    ///   RECEPTION  — front-desk / reception coverage
    /// Only meaningful when EmployeeType = Substitute. NULL is treated as
    /// TEACHER by application code for backward compatibility with the
    /// legacy sub pool that existed before this migration.
    /// </summary>
    public string? SubRole { get; set; }

    /// <summary>
    /// Migration 045 (Phase D5): supervisor-managed exclusion. When true,
    /// SubOutreachPanel will skip this sub when listing candidates, even
    /// though they remain in the Entra group + active in TcEmployees.
    /// Used when a sub didn't meet expectations but HR doesn't want to
    /// remove them from the group entirely.
    /// </summary>
    public bool ExcludedFromSubPool { get; set; } = false;
    public string? ExcludedFromSubPoolBy { get; set; }
    public DateTime? ExcludedFromSubPoolDate { get; set; }
    public string? ExcludedFromSubPoolReason { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    public Staff? Staff { get; set; }
    public Campus? HomeCampus { get; set; }
    public TcEmployee? Supervisor { get; set; }
    public TcPayRule? PayRule { get; set; }
    public ICollection<TcEmployee> DirectReports { get; set; } = new List<TcEmployee>();
    public ICollection<TcTimePunch> TimePunches { get; set; } = new List<TcTimePunch>();
    public ICollection<TcDailyTimecard> DailyTimecards { get; set; } = new List<TcDailyTimecard>();
    public ICollection<TcPayPeriodSummary> PayPeriodSummaries { get; set; } = new List<TcPayPeriodSummary>();
    public ICollection<TcCorrectionRequest> CorrectionRequests { get; set; } = new List<TcCorrectionRequest>();
    public ICollection<TcSubRequest> SubRequests { get; set; } = new List<TcSubRequest>();
    public ICollection<TcCalendarEvent> CalendarEvents { get; set; } = new List<TcCalendarEvent>();
    public ICollection<TcNotification> Notifications { get; set; } = new List<TcNotification>();
}
