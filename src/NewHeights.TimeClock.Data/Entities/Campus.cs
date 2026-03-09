namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Maps to existing Attendance_Campuses table (will be ALTERED to add geofence columns)
/// </summary>
public class Campus
{
    public int CampusId { get; set; }
    public required string CampusCode { get; set; }
    public required string CampusName { get; set; }
    public int? PowerSchoolId { get; set; }
    public string? SchoolNameValue { get; set; }
    
    // New geofence columns to be added
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public int GeofenceRadiusMeters { get; set; } = 150;
    public string? CampusWifiSSID { get; set; }
    public TimeOnly? DefaultStartTime { get; set; }
    public TimeOnly? DefaultEndTime { get; set; }
    public TimeOnly? LunchStartTime { get; set; }
    public TimeOnly? LunchEndTime { get; set; }
    public int GracePeriodMinutes { get; set; } = 5;
    
    // Navigation properties
    public ICollection<TcEmployee> Employees { get; set; } = new List<TcEmployee>();
    public ICollection<TcTimePunch> TimePunches { get; set; } = new List<TcTimePunch>();
}
