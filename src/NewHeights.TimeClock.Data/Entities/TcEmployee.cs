using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcEmployee
{
    public int EmployeeId { get; set; }
    public int StaffDcid { get; set; }
    public required string IdNumber { get; set; }
    public string? AscenderEmployeeId { get; set; }
    public EmployeeType EmployeeType { get; set; } = EmployeeType.HourlyStaff;
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
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

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
