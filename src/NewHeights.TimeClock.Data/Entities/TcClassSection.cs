namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Canonical class-section identity for class-period attendance.
/// Keyed to PowerSchool's sections.id (the URL parameter on PS's
/// /teachers/attendance-grid.action page) so the static room QR codes
/// can encode the PS section id directly and survive re-imports.
///
/// Populated by IClassRosterService (Phase B) via a cross-DB read
/// against CaseManagementDB.dbo.Advising_StudentSchedule, with a
/// PowerSchool named-query fallback when the section isn't cached.
///
/// DistrictId and CampusId are denormalized from the campus/district
/// hierarchy so receptionist and district reports don't need
/// multi-hop joins.
/// </summary>
public class TcClassSection
{
    public int ClassSectionId { get; set; }

    /// <summary>
    /// PowerSchool sections.id (Oracle NUMBER, stored as BIGINT here).
    /// Unique across all districts (PS keeps this unique per instance).
    /// </summary>
    public long PsSectionId { get; set; }

    public int DistrictId { get; set; } = 1;
    public int CampusId { get; set; }

    /// <summary>PowerSchool schoolid (e.g. 220822001 for Stop Six).</summary>
    public long? PsSchoolId { get; set; }

    public required string CourseNumber { get; set; }
    public required string CourseName { get; set; }
    public string? SectionNumber { get; set; }

    /// <summary>
    /// PS sections.external_expression - e.g. "MW-F" or "TTh-1".
    /// Free-text per PS; do NOT parse as enum.
    /// </summary>
    public string? Expression { get; set; }

    public int? PeriodNumber { get; set; }
    public string? Room { get; set; }

    /// <summary>
    /// Soft reference to Staff.Dcid. No FK because Staff is PS-sourced
    /// and rows can disappear on a sync. Resolves via navigation when
    /// the row exists.
    /// </summary>
    public int? TeacherDcid { get; set; }

    /// <summary>
    /// TC convention: TERM1 / TERM2 / TERM3 / TERM4. See
    /// reference_master_schedule_format memory for the
    /// short-form (T1) vs long-form normalization rule.
    /// </summary>
    public string? TermName { get; set; }

    /// <summary>Short-form school year: '2025-26'.</summary>
    public string? SchoolYear { get; set; }

    /// <summary>PS terms.id - kept for direct PS API joins.</summary>
    public int? TermId { get; set; }

    /// <summary>
    /// Optional cross-ref to TC_MasterSchedule.ScheduleId when our
    /// own import has a matching row. NULL is fine - PS is the
    /// authoritative source for sections.
    /// </summary>
    public int? MasterScheduleId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime LastSyncDate { get; set; } = DateTime.Now;
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? ModifiedDate { get; set; }

    // Navigation
    public District? District { get; set; }
    public Campus? Campus { get; set; }
    public Staff? Teacher { get; set; }
    public TcMasterSchedule? MasterSchedule { get; set; }
}
