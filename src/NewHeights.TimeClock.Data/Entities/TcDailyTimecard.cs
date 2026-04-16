using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcDailyTimecard
{
    public long TimecardId { get; set; }
    public int EmployeeId { get; set; }
    public int CampusId { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTime? FirstPunchIn { get; set; }
    public DateTime? LastPunchOut { get; set; }
    public decimal RegularHours { get; set; } = 0;
    public decimal OvertimeHours { get; set; } = 0;
    public decimal TotalHours { get; set; } = 0;
    public int LunchMinutes { get; set; } = 0;
    public int BreakMinutes { get; set; } = 0;
    public TimeOnly? ScheduledStartTime { get; set; }
    public TimeOnly? ScheduledEndTime { get; set; }
    public decimal? ScheduledHours { get; set; }
    public int VarianceMinutes { get; set; } = 0;
    public bool IsLateArrival { get; set; } = false;
    public bool IsEarlyDeparture { get; set; } = false;
    public bool IsMissedPunch { get; set; } = false;
    public bool IsAbsent { get; set; } = false;
    public bool HasException { get; set; } = false;
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }
    public string? ExceptionNotes { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    // Navigation
    public TcEmployee? Employee { get; set; }
    public Campus? Campus { get; set; }
}
