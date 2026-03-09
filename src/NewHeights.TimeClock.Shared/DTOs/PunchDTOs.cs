namespace NewHeights.TimeClock.Shared.DTOs;

public class PunchRequest
{
    public string IdNumber { get; set; } = string.Empty;
    public int CampusId { get; set; }
    public string? CampusCode { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? WifiSSID { get; set; }
    public int? TerminalId { get; set; }
    public string ScanMethod { get; set; } = "QR";
    public bool IsMobileMode { get; set; }
    public string? LoggedInUserIdNumber { get; set; }
}

public class PunchResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? EmployeeName { get; set; }
    public string? EmployeePhotoBase64 { get; set; }
    public string? PunchType { get; set; }
    public DateTime? PunchTime { get; set; }
    public DateTime? RoundedTime { get; set; }
    public decimal? TotalHoursToday { get; set; }
    public string? GeofenceStatus { get; set; }
    public string? ErrorCode { get; set; }
    public bool NoPhotoOnFile { get; set; }
}

public class EmployeeLookupResult
{
    public bool Found { get; set; }
    public int? EmployeeId { get; set; }
    public int? StaffDcid { get; set; }
    public string? IdNumber { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? FullName { get; set; }
    public string? JobTitle { get; set; }
    public string? PhotoBase64 { get; set; }
    public int? HomeCampusId { get; set; }
    public string? EmployeeType { get; set; }
}

public class GeofenceCheckResult
{
    public bool IsWithinGeofence { get; set; }
    public string Status { get; set; } = string.Empty;
    public string VerificationMethod { get; set; } = string.Empty;
    public double? DistanceMeters { get; set; }
    public string? Message { get; set; }
}

public class CampusInfo
{
    public int CampusId { get; set; }
    public string CampusCode { get; set; } = string.Empty;
    public string CampusName { get; set; } = string.Empty;
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public int GeofenceRadiusMeters { get; set; }
    public string? WifiSSID { get; set; }
    public TimeSpan? DefaultStartTime { get; set; }
    public TimeSpan? DefaultEndTime { get; set; }
    public TimeSpan? LunchStartTime { get; set; }
    public TimeSpan? LunchEndTime { get; set; }
    public int GracePeriodMinutes { get; set; }
}
