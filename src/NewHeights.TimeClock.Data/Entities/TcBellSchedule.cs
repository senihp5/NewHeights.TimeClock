namespace NewHeights.TimeClock.Data.Entities;

public class TcBellSchedule
{
    public int BellScheduleId { get; set; }
    public int CampusId { get; set; }
    public string SchoolYear { get; set; } = string.Empty;
    public string ScheduleName { get; set; } = string.Empty;

    // DAY or NIGHT
    public string SessionType { get; set; } = "DAY";

    // STANDARD, LATE_START, EARLY_RELEASE, TESTING, CUSTOM
    public string ScheduleType { get; set; } = "STANDARD";

    public DateOnly? EffectiveStartDate { get; set; }
    public DateOnly? EffectiveEndDate { get; set; }
    public bool IsDefault { get; set; } = false;
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public string? UploadedBy { get; set; }
    public DateTime? UploadedDate { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public Campus? Campus { get; set; }
    public ICollection<TcBellPeriod> Periods { get; set; } = new List<TcBellPeriod>();
}
