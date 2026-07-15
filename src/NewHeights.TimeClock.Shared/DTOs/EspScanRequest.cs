namespace NewHeights.TimeClock.Shared.DTOs;

/// <summary>
/// Request body POSTed by the ESP32 WiFi scanner to /api/v1/punch.
/// RawScan is the literal string emitted by the QR decoder
/// (typically pipe-delimited "FirstName|LastName|012345").
/// Server runs the same IKioskScanService.ProcessRawScanAsync path
/// used by the wired kiosk page.
/// </summary>
public class EspScanRequest
{
    public string RawScan { get; set; } = string.Empty;
    public string? CampusCode { get; set; }
    public int? CampusId { get; set; }
    public int? TerminalId { get; set; }
    public string? ScanMethod { get; set; }
}
