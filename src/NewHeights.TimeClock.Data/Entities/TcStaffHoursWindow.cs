namespace NewHeights.TimeClock.Data.Entities;

public class TcStaffHoursWindow
{
    public int WindowId { get; set; }
    public int CampusId { get; set; }

    // DAY or NIGHT
    public string SessionType { get; set; } = "DAY";

    public string SchoolYear { get; set; } = string.Empty;

    public TimeOnly ExpectedArrivalTime { get; set; }
    public TimeOnly ExpectedDepartureTime { get; set; }

    public int LateArrivalThresholdMin { get; set; } = 5;
    public int EarlyDepartureThresholdMin { get; set; } = 10;
    public int MissedPunchAlertMin { get; set; } = 15;

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    // Navigation
    public Campus? Campus { get; set; }
}
