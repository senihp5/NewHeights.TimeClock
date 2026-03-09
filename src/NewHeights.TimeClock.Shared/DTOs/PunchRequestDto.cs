using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Shared.DTOs;

public class PunchRequestDto
{
    public required string IdNumber { get; set; }
    public int CampusId { get; set; }
    public int? TerminalId { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? WifiSSID { get; set; }
    public PunchType? ForcePunchType { get; set; }
}
