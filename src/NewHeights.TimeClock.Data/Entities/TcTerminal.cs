namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Physical scanning device registered to a campus + location.
/// Resolves AttendanceTransaction.TerminalId / LocationId / CampusId
/// for every scan from a known device (wired kiosk, ESP32 WiFi scanner,
/// tablet, etc.).
///
/// Each ESP32 WiFi scanner posts a TerminalId in its /api/v1/punch body;
/// the server looks up this row to resolve campus + location and verify
/// the terminal is active.
/// </summary>
public class TcTerminal
{
    public int TerminalId { get; set; }
    public string TerminalCode { get; set; } = string.Empty;
    public int CampusId { get; set; }
    public int LocationId { get; set; } = 1;
    public string LocationDescription { get; set; } = string.Empty;
    public string DeviceType { get; set; } = "ESP32_KIOSK";
    public string TerminalPurpose { get; set; } = "CAMPUS_CHECKIN";
    public string? DeviceSecretHash { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public string? LastSeenFirmware { get; set; }

    // 2026-07-08 (migration 069): per-terminal opt-in for front-camera photo
    // capture at scan time on TABLET_KIOSK devices. Default false everywhere.
    // The tablet page reads this at init and only opens the front camera
    // when true. ESP32 kiosks ignore this flag entirely — they handle
    // photo capture in firmware.
    public bool PhotoCaptureEnabled { get; set; } = false;

    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? ModifiedDate { get; set; }

    // Navigation
    public Campus? Campus { get; set; }
}
