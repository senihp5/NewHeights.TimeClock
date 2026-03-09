using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Shared.DTOs;

public class TimecardDto
{
    public long TimecardId { get; set; }
    public int EmployeeId { get; set; }
    public required string EmployeeName { get; set; }
    public DateOnly WorkDate { get; set; }
    public DateTime? FirstPunchIn { get; set; }
    public DateTime? LastPunchOut { get; set; }
    public decimal RegularHours { get; set; }
    public decimal OvertimeHours { get; set; }
    public decimal TotalHours { get; set; }
    public int LunchMinutes { get; set; }
    public bool IsLateArrival { get; set; }
    public bool IsEarlyDeparture { get; set; }
    public bool IsMissedPunch { get; set; }
    public bool HasException { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; }
    public List<PunchDetailDto> Punches { get; set; } = new();
}

public class PunchDetailDto
{
    public long PunchId { get; set; }
    public PunchType PunchType { get; set; }
    public DateTime PunchDateTime { get; set; }
    public DateTime? RoundedDateTime { get; set; }
    public GeofenceStatus GeofenceStatus { get; set; }
    public bool IsManualEntry { get; set; }
    public bool IsModified { get; set; }
    public string? Notes { get; set; }
}
