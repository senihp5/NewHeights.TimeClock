namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Represents one teacher's schedule row in the campus master schedule.
/// Populated by the schedule import tool at /admin/schedule/import.
/// Teacher names from the spreadsheet are resolved to Staff.Dcid references
/// during import; unmatched names are flagged for manual correction.
/// </summary>
public class TcMasterSchedule
{
    public int ScheduleId { get; set; }

    // ── Context ──────────────────────────────────────────────────────────
    public int CampusId { get; set; }
    public string TermName { get; set; } = string.Empty;      // TERM2, TERM3, TERM4
    public string SchoolYear { get; set; } = string.Empty;    // 2025-26

    // ── Raw import data (preserved verbatim for audit / re-import) ───────
    public string? RawTeacherCell { get; set; }
    public string? RawPartnerNames { get; set; }

    // ── Resolved staff references ─────────────────────────────────────────
    /// <summary>Staff.Dcid of the teacher of record. NULL if not yet matched.</summary>
    public int? TeacherStaffDcid { get; set; }

    /// <summary>Staff.Dcid of the first partner teacher (after '-' in cell). NULL if n/a or unmatched.</summary>
    public int? Partner1StaffDcid { get; set; }

    /// <summary>Staff.Dcid of the second partner teacher (after '/' in cell). NULL if n/a or unmatched.</summary>
    public int? Partner2StaffDcid { get; set; }

    // ── Schedule metadata ─────────────────────────────────────────────────
    public string? Room { get; set; }

    /// <summary>DAY, MW, TTH, DAY/TTH</summary>
    public string DayPattern { get; set; } = string.Empty;

    /// <summary>DAY or EVENING — derived from which period columns are populated.</summary>
    public string Shift { get; set; } = "DAY";

    /// <summary>CTE, MATH, ELAR, SCI, SS — from the CONT column in Term 2 format.</summary>
    public string? ContentArea { get; set; }

    // ── Mon/Wed course assignments per period ─────────────────────────────
    public string? MW_P1 { get; set; }
    public string? MW_P2 { get; set; }
    public string? MW_P3 { get; set; }
    public string? MW_P4 { get; set; }
    public string? MW_P5 { get; set; }
    public string? MW_P6 { get; set; }

    // ── Tue/Thu course assignments per period ─────────────────────────────
    public string? TTh_P1 { get; set; }
    public string? TTh_P2 { get; set; }
    public string? TTh_P3 { get; set; }
    public string? TTh_P4 { get; set; }
    public string? TTh_P5 { get; set; }
    public string? TTh_P6 { get; set; }

    // ── Match confidence ──────────────────────────────────────────────────
    /// <summary>Exact | LastName | Fuzzy | Manual | Unmatched</summary>
    public string? TeacherMatchMethod { get; set; }
    public string? Partner1MatchMethod { get; set; }
    public string? Partner2MatchMethod { get; set; }

    // ── Lifecycle ─────────────────────────────────────────────────────────
    public bool IsActive { get; set; } = true;
    public DateTime ImportedDate { get; set; } = DateTime.Now;
    public string? ImportedBy { get; set; }
    public string? Notes { get; set; }

    // ── Navigation ────────────────────────────────────────────────────────
    public Campus? Campus { get; set; }
    public Staff? Teacher { get; set; }
    public Staff? Partner1 { get; set; }
    public Staff? Partner2 { get; set; }
}
