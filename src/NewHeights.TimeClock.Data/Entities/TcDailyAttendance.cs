using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcDailyAttendance
{
    public long AttendanceId { get; set; }
    public int EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    
    // Attendance Status for each shift
    public AttendanceStatus AMStatus { get; set; } = AttendanceStatus.NotApplicable;
    public AttendanceStatus PMStatus { get; set; } = AttendanceStatus.NotApplicable;
    public AttendanceStatus EveningStatus { get; set; } = AttendanceStatus.NotApplicable;
    
    // Hours
    public decimal HoursWorked { get; set; } = 0;
    public decimal LeaveHours { get; set; } = 0;
    public decimal HolidayHours { get; set; } = 0;
    public decimal TotalHours { get; set; } = 0;
    
    // Comments and certification
    public string? Comments { get; set; }
    public bool IsCertified { get; set; } = false;
    public DateTime? CertifiedDate { get; set; }
    public string? CertifiedBy { get; set; }
    
    // Approval workflow
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public string? SupervisorApprovedBy { get; set; }
    public DateTime? SupervisorApprovedDate { get; set; }
    public string? SupervisorNotes { get; set; }
    
    // Audit
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
    public string? ModifiedBy { get; set; }

    // Navigation
    public TcEmployee? Employee { get; set; }
}
