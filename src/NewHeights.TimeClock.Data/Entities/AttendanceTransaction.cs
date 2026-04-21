namespace NewHeights.TimeClock.Data.Entities;

public class AttendanceTransaction
{
    public long TransactionId { get; set; }
    public string TransactionType { get; set; } = string.Empty;  // STAFF, STUDENT
    public string IdNumber { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int CampusId { get; set; }
    public int LocationId { get; set; }
    public DateTime ScanDateTime { get; set; }
    public string ScanType { get; set; } = string.Empty;  // CAMPUS_IN, CAMPUS_OUT, LUNCH_OUT, LUNCH_IN
    public string ScanMethod { get; set; } = string.Empty;  // QR, NFC, MANUAL, GPS
    public string? QRCodeScanned { get; set; }
    public int TerminalId { get; set; }
    public int? PowerSchoolSchoolId { get; set; }
    public int? PowerSchoolEnrollStatus { get; set; }
    public string DataSource { get; set; } = string.Empty;  // POWERSCHOOL, LOCAL
    public string ValidationStatus { get; set; } = string.Empty;  // VALID, INVALID
    public string? ValidationMessage { get; set; }
    public string? Notes { get; set; }

    // LUNCH, MEDICAL, MEETING, PERSONAL, EMERGENCY (early-out reason)
    // RETURN_LUNCH, RETURN_MEDICAL, etc. (same-day return punch)
    public string? PunchSubType { get; set; }

    // Migration 039: GPS proof captured at the moment of the scan. Populated
    // by geofence-gated paths (StudentCheckin, MobileCheckin); null for the
    // kiosk path (physical device presence IS the validation) and any admin
    // manual override.
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public decimal? LocationAccuracyMeters { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;

    // Navigation
    public Campus? Campus { get; set; }
}
