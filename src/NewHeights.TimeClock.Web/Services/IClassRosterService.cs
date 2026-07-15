using NewHeights.TimeClock.Data.Entities;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Reads section + enrollment data from CMS (cross-DB to
/// CaseManagementDB.dbo.Advising_StudentSchedule / Advising_MasterSchedule)
/// and lazy-materializes it into local TC_ClassSection + TC_SectionEnrollment
/// tables on first reference. Subsequent reads serve from local cache.
///
/// LastSyncDate columns on both tables let a future background refresh
/// task (Phase B+) propagate teacher / course / room changes from upstream
/// PowerSchool data without forcing every read to hit CMS.
///
/// Cross-DB query pattern follows MasterScheduleLookupService - the
/// DefaultConnection's SQL login has SELECT permission on both databases,
/// so three-part name joins (CaseManagementDB.dbo.&lt;table&gt;) work without
/// a separate DbContext.
/// </summary>
public interface IClassRosterService
{
    /// <summary>
    /// Returns the sections a teacher is teaching on a given date,
    /// scoped to their resolved home campus. Pulls from local cache
    /// when fresh; materializes from CMS when missing.
    ///
    /// Result is denormalized into a summary DTO so the /teacher/today
    /// landing page can render cards without additional joins.
    /// </summary>
    Task<List<ClassSectionSummary>> GetSectionsForTeacherAsync(
        int teacherDcid,
        DateOnly date,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the single section keyed by PowerSchool section id.
    /// Used by the static room QR landing page in Phase E and the
    /// URL deep-link from /teacher/today.
    ///
    /// Returns null if the PS section can't be resolved in CMS either
    /// (e.g. invalid QR scan, section ended last term).
    /// </summary>
    Task<TcClassSection?> GetSectionAsync(
        long psSectionId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the active roster for a section on a date. Each row
    /// carries the denormalized student name + number so the caller
    /// never needs to JOIN to Students for display.
    ///
    /// Active = TC_SectionEnrollment.IsActive=1 AND
    ///         (DateLeft IS NULL OR DateLeft >= @date) AND
    ///         (DateEnrolled IS NULL OR DateEnrolled &lt;= @date).
    /// </summary>
    Task<List<EnrollmentRow>> GetRosterAsync(
        int classSectionId,
        DateOnly date,
        CancellationToken ct = default);

    /// <summary>
    /// Ensures a TC_ClassSection row exists for the given PS section id,
    /// pulling from CMS + populating TC_SectionEnrollment if not yet
    /// cached. Idempotent. Returns the local ClassSectionId.
    ///
    /// Used by the teacher landing page when a section first becomes
    /// visible, by the room QR scan flow, and by background refresh.
    /// </summary>
    Task<int> EnsureSectionCachedAsync(
        long psSectionId,
        CancellationToken ct = default);

    /// <summary>
    /// Phase E hook: given a student's badge number and the local time
    /// of a QR scan, resolves which section they should be checking
    /// into right now based on bell schedule + enrollment. Returns null
    /// if no section matches the time window or the student isn't
    /// enrolled in any concurrent class.
    /// </summary>
    Task<TcClassSection?> ResolveCurrentSectionForStudentAsync(
        string studentNumber,
        DateTime nowLocal,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a signed-in user's email (Entra OAuth claim) to their
    /// local Staff.Dcid by looking up CaseManagementDB.dbo.Core_Staff.
    /// Returns null if the email isn't a known staff member, the staff
    /// member is inactive, or CMS is unreachable.
    ///
    /// Used by /teacher/today as the bridge between auth claims and
    /// the teacher-of-record references in TC_ClassSection.
    /// </summary>
    Task<int?> ResolveTeacherDcidFromEmailAsync(
        string email,
        CancellationToken ct = default);
}

/// <summary>
/// Card-sized projection for the /teacher/today landing page.
/// Carries enough to render the section selection UI without extra
/// joins: course, period, room, time, optional sheet workflow status.
/// </summary>
public class ClassSectionSummary
{
    public int ClassSectionId { get; set; }
    public long PsSectionId { get; set; }
    public string CourseName { get; set; } = string.Empty;
    public string? CourseNumber { get; set; }
    public int? PeriodNumber { get; set; }
    public string? Room { get; set; }
    public string? Expression { get; set; }
    public int CampusId { get; set; }
    public string? CampusName { get; set; }
    public string? TermName { get; set; }
    public string? SchoolYear { get; set; }

    /// <summary>
    /// Enrolled student count on the queried date. Computed during
    /// the summary query so the card can show "23 students" without
    /// a second round-trip.
    /// </summary>
    public int EnrolledCount { get; set; }

    /// <summary>
    /// The sheet row for this section on the queried date, if it
    /// exists. NULL means the teacher hasn't opened the section yet.
    /// Status pill on the card uses Sheet.Status when present and
    /// "NotStarted" (default) when null.
    /// </summary>
    public TcClassAttendanceSheet? Sheet { get; set; }
}

/// <summary>
/// Roster row for the teacher attendance grid. Carries the
/// denormalized name + number from TC_SectionEnrollment so the
/// grid renders without joining Students.
/// </summary>
public class EnrollmentRow
{
    public int EnrollmentId { get; set; }
    public int StudentDcid { get; set; }
    public string StudentNumber { get; set; } = string.Empty;
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public DateOnly? DateEnrolled { get; set; }
    public DateOnly? DateLeft { get; set; }

    public string FullName =>
        string.IsNullOrWhiteSpace(LastName) && string.IsNullOrWhiteSpace(FirstName)
            ? StudentNumber
            : $"{LastName}, {FirstName}".Trim().TrimEnd(',');

    public string DisplayName =>
        string.IsNullOrWhiteSpace(FirstName) && string.IsNullOrWhiteSpace(LastName)
            ? $"#{StudentNumber}"
            : $"{FirstName} {LastName}".Trim();
}
