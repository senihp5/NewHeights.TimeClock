using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcTimePunch
{
    public long PunchId { get; set; }
    public int EmployeeId { get; set; }
    public int CampusId { get; set; }
    public int? TerminalId { get; set; }
    public PunchType PunchType { get; set; }
    public DateTime PunchDateTime { get; set; }
    public DateTime? RoundedDateTime { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public GeofenceStatus GeofenceStatus { get; set; } = GeofenceStatus.Verified;
    public required string VerificationMethod { get; set; } = "GPS";
    public required string ScanMethod { get; set; } = "QR";
    public string? QRCodeScanned { get; set; }
    public PunchStatus PunchStatus { get; set; } = PunchStatus.Active;
    public long? PairedPunchId { get; set; }
    public bool IsManualEntry { get; set; } = false;
    public bool IsModified { get; set; } = false;
    public DateTime? OriginalPunchDateTime { get; set; }
    public string? ModifiedBy { get; set; }
    public string? ModifiedReason { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public TcEmployee? Employee { get; set; }
    public Campus? Campus { get; set; }
    public TcTimePunch? PairedPunch { get; set; }
}
