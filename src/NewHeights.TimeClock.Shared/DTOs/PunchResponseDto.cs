using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Shared.DTOs;

public class PunchResponseDto
{
    public long PunchId { get; set; }
    public int EmployeeId { get; set; }
    public required string EmployeeName { get; set; }
    public string? PhotoBase64 { get; set; }
    public PunchType PunchType { get; set; }
    public DateTime PunchDateTime { get; set; }
    public DateTime? RoundedDateTime { get; set; }
    public GeofenceStatus GeofenceStatus { get; set; }
    public string? Message { get; set; }
    public decimal? TotalHoursToday { get; set; }
}
