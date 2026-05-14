namespace NewHeights.TimeClock.Shared.DTOs;

/// <summary>
/// Result of processing a raw QR scan through the kiosk dispatch logic.
/// Returned by IKioskScanService.ProcessRawScanAsync.
/// Consumed by both the Blazor kiosk page (translates to component UI state)
/// and any external API endpoint (serializes as JSON to ESP32 scanners).
/// </summary>
public class KioskScanResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    public string? PersonType { get; set; }
    public string? PersonName { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? IdNumber { get; set; }
    public string? PhotoBase64 { get; set; }

    public string? ScanType { get; set; }
    public string? ScanTypeDisplay { get; set; }
    public string? ScanTypeBadgeClass { get; set; }
    public string? PersonTypeDisplay { get; set; }

    public decimal? TotalHoursToday { get; set; }
    public DateTime? ScanTime { get; set; }
    public string? ErrorCode { get; set; }
}
