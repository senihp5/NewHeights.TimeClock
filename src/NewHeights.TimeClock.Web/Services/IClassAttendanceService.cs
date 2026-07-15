using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Reads + writes cell-level attendance state and sheet workflow on
/// TC_ClassAttendance and TC_ClassAttendanceSheet. Used by:
///   - /class/attendance/{sectionId}/{date} (teacher grid + submit)
///   - /class/checkin (Phase E student QR scan)
///   - /reception/class-attendance (Phase D validate/reject)
/// </summary>
public interface IClassAttendanceService
{
    /// <summary>
    /// Ensures a TC_ClassAttendanceSheet row exists for (classSectionId, date).
    /// Created with Status = NotStarted on first reference. Subsequent calls
    /// return the existing row. Idempotent.
    /// </summary>
    Task<TcClassAttendanceSheet> EnsureSheetAsync(
        int classSectionId, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Returns all TC_ClassAttendance rows for (classSectionId, date).
    /// May return an empty list if the teacher hasn't marked anything yet.
    /// </summary>
    Task<List<TcClassAttendance>> GetCellsForSectionDateAsync(
        int classSectionId, DateOnly date, CancellationToken ct = default);

    /// <summary>
    /// Insert-or-update a single TC_ClassAttendance row. Driven by the
    /// teacher tapping a status pill, a student QR scan, or a classroom
    /// kiosk scan. Auto-transitions sheet Status to InProgress on first
    /// non-default mark.
    /// </summary>
    Task MarkAsync(
        int classSectionId,
        int studentDcid,
        string studentNumber,
        DateOnly date,
        ClassAttendanceStatus status,
        string? comment,
        ClassAttendanceSource source,
        DateTime? scannedAt,
        int? minutesLate,
        string markedBy,
        CancellationToken ct = default);

    /// <summary>
    /// Teacher action: transitions sheet from InProgress -> Submitted.
    /// Captures snapshot counts (PresentCount, TardyCount, ...). Sends
    /// nothing to PS - PS is updated by reception during validation.
    /// </summary>
    Task SubmitSheetAsync(
        int sheetId, string submittedBy, CancellationToken ct = default);
}
