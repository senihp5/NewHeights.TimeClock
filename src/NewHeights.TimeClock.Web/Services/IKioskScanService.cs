using NewHeights.TimeClock.Data.Entities;
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
        string scanMethod,
        int terminalId = 0,
        int locationId = 1);

    /// <summary>
    /// 2026-07-08: Look up an active TC_Terminals row by its TerminalCode.
    /// Used by the tablet kiosk page's route-parameter resolution
    /// (/kiosk/tablet/{terminalCode}) to validate the URL segment on every
    /// render — a null return means the terminal doesn't exist, is
    /// deactivated (IsActive=0), or its DeviceType doesn't match the
    /// caller's expected type (see optional <paramref name="expectedDeviceType"/>).
    /// Case-insensitive match. Read-only, no side effects.
    /// </summary>
    /// <param name="terminalCode">The unique code embedded in the URL.</param>
    /// <param name="expectedDeviceType">
    /// Optional device-type filter (e.g. AppConstants.Kiosk.DeviceType.TabletKiosk).
    /// When set, a terminal whose DeviceType doesn't match returns null even
    /// if it exists and is active.
    /// </param>
    Task<TcTerminal?> ResolveActiveTerminalAsync(string terminalCode, string? expectedDeviceType = null);
}
