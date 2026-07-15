namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// One row per (student, section, enrollment-span) - mirrors the
/// PowerSchool [cc] (course-class) table. When a student withdraws
/// then re-enrolls in the same section, PS creates a new cc row;
/// we follow suit so enrollment history matches PS exactly.
///
/// Active enrollment is any row where DateLeft IS NULL. The
/// filtered unique index on (ClassSectionId, StudentDcid) WHERE
/// DateLeft IS NULL prevents duplicate active enrollments without
/// blocking re-enrollment after a withdrawal.
///
/// StudentLastName / StudentFirstName / StudentNumber are
/// denormalized from Students to avoid a JOIN on every roster
/// render. Refreshed by IClassRosterService on sync; if a student
/// is renamed in PS, the next sync picks it up.
/// </summary>
public class TcSectionEnrollment
{
    public int EnrollmentId { get; set; }

    public int ClassSectionId { get; set; }

    /// <summary>
    /// Soft reference to Students.Dcid. No SQL FK because Students
    /// is PS-sourced and rows can disappear on a sync. EF nav loads
    /// when the row exists.
    /// </summary>
    public int StudentDcid { get; set; }

    /// <summary>
    /// Denormalized from Students.IdNumber (the badge / QR payload).
    /// Indexed for scan-by-badge lookups in Phase E's student QR flow.
    /// </summary>
    public required string StudentNumber { get; set; }

    public string? StudentLastName { get; set; }
    public string? StudentFirstName { get; set; }

    public int DistrictId { get; set; } = 1;
    public int CampusId { get; set; }

    public DateOnly? DateEnrolled { get; set; }

    /// <summary>NULL = student is still actively enrolled.</summary>
    public DateOnly? DateLeft { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime LastSyncDate { get; set; } = DateTime.Now;
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? ModifiedDate { get; set; }

    // Navigation
    public TcClassSection? ClassSection { get; set; }
    public Student? Student { get; set; }
    public District? District { get; set; }
    public Campus? Campus { get; set; }
}
