using NewHeights.TimeClock.Shared.DTOs;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Centralized scan-dispatch service used by both the Blazor kiosk page
/// and any REST endpoint (e.g. ESP32 WiFi scanner /api/v1/punch).
///
/// Parses pipe-delimited QR payloads (FirstName|LastName|IdNumber),
/// disambiguates between Staff (salaried), Hourly Employee, and Student
/// records, dispatches to the appropriate write path
/// (AttendanceTransaction and/or TcTimePunch), and returns a fully-formed
/// KioskScanResult ready for UI rendering or JSON serialization.
/// </summary>
public interface IKioskScanService
{
    Task<KioskScanResult> ProcessRawScanAsync(
        string rawScan,
        int campusId,
        string scanMethod);
}
