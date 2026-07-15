using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Per-(ClassSectionId, SheetDate) header row that drives the entire
/// class-attendance workflow. One sheet per section per day.
///
/// Status state machine:
///   NotStarted -> InProgress -> Submitted -> Validated (terminal)
///                                   |
///                                   v
///                                Rejected -> (teacher fixes) -> InProgress
///   Validated -> Reopened (CampusAdmin+ only) -> InProgress
///
/// Validate IS the PS reconciliation marker. When reception confirms PS
/// matches the sheet, the row's Validated* columns get populated. There
/// is no separate TransferredToPs flag.
///
/// PDF archive (Phase D): on Validate, IClassSheetArchiveService renders
/// a QuestPDF and uploads to Azure Blob (newheightscmsstorage /
/// container class-attendance-archives). ArchivePdfBlobUri holds the
/// full https URI; the receptionist + campus admin history views use it
/// for the "Download Archive" link.
/// </summary>
public class TcClassAttendanceSheet
{
    public int SheetId { get; set; }

    public int ClassSectionId { get; set; }
    public DateOnly SheetDate { get; set; }

    public int DistrictId { get; set; } = 1;
    public int CampusId { get; set; }

    /// <summary>
    /// Denormalized from TC_ClassSection.TeacherDcid for the
    /// per-teacher completeness reports without an extra JOIN.
    /// Soft reference to Staff.Dcid - no FK because Staff is PS-sourced.
    /// </summary>
    public int? TeacherDcid { get; set; }

    public ClassAttendanceSheetStatus Status { get; set; } = ClassAttendanceSheetStatus.NotStarted;

    // ── Submit (teacher action) ────────────────────────────────────────
    public DateTime? SubmittedAt { get; set; }
    public string? SubmittedBy { get; set; }

    // ── Validate (receptionist action - PS reconciled) ────────────────
    public DateTime? ValidatedAt { get; set; }
    public string? ValidatedBy { get; set; }
    public string? ValidationNote { get; set; }

    // ── Reject (receptionist action - send back to teacher) ───────────
    public DateTime? RejectedAt { get; set; }
    public string? RejectedBy { get; set; }
    public string? RejectionReason { get; set; }

    // ── Reopen (CampusAdmin+ override) ────────────────────────────────
    public DateTime? ReopenedAt { get; set; }
    public string? ReopenedBy { get; set; }
    public string? ReopenReason { get; set; }

    /// <summary>Audit of teacher resubmits after Reject events.</summary>
    public int TeacherResubmitCount { get; set; } = 0;

    // ── Snapshot counts (populated at Submit, refreshed at Validate) ──
    public int? PresentCount { get; set; }
    public int? TardyCount { get; set; }
    public int? AbsentCount { get; set; }
    public int? ExcusedCount { get; set; }
    public int? EarlyOutCount { get; set; }
    public int? EnrolledCount { get; set; }

    // ── PDF archive (Azure Blob) ──────────────────────────────────────
    public string? ArchivePdfBlobUri { get; set; }
    public DateTime? ArchivedAt { get; set; }

    // ── Audit ─────────────────────────────────────────────────────────
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? ModifiedDate { get; set; }

    // Navigation
    public TcClassSection? ClassSection { get; set; }
    public District? District { get; set; }
    public Campus? Campus { get; set; }
    public Staff? Teacher { get; set; }
}
