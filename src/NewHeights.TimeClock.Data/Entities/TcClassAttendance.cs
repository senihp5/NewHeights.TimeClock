using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Cell-level attendance grain - one row per (student, section, date).
/// The actual attendance state a teacher records and a receptionist
/// reconciles against PowerSchool.
///
/// PsAttendanceCode is a PERSISTED computed column maintained by SQL,
/// not C#. The C# property is marked DatabaseGenerated so EF treats
/// it as read-only and never tries to insert a value.
/// </summary>
public class TcClassAttendance
{
    public long AttendanceId { get; set; }

    public int ClassSectionId { get; set; }
    public int StudentDcid { get; set; }
    public required string StudentNumber { get; set; }
    public DateOnly AttendanceDate { get; set; }

    public int DistrictId { get; set; } = 1;
    public int CampusId { get; set; }

    public ClassAttendanceStatus Status { get; set; } = ClassAttendanceStatus.Present;
    public string? Comment { get; set; }

    /// <summary>
    /// When the student scanned a QR / kiosk, if applicable. NULL for
    /// teacher-manual rows.
    /// </summary>
    public DateTime? ScannedAt { get; set; }

    /// <summary>
    /// Derived at scan time: ScannedAt - TcBellPeriod.StartTime, in minutes.
    /// NULL when ScannedAt is null OR when the bell period can't be resolved.
    /// </summary>
    public int? MinutesLate { get; set; }

    public ClassAttendanceSource Source { get; set; }

    /// <summary>
    /// Teacher email when Source = TeacherManual / BulkPresent.
    /// Student email when Source = QrPhone.
    /// 'system' for Inferred / ClassroomKiosk (no human directly drove it).
    /// </summary>
    public required string MarkedBy { get; set; }

    /// <summary>
    /// PERSISTED computed column maintained by SQL. Never set this in C#;
    /// EF is configured to treat it as read-only via OnModelCreating.
    /// </summary>
    public string PsAttendanceCode { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? ModifiedDate { get; set; }

    // Navigation
    public TcClassSection? ClassSection { get; set; }
    public Student? Student { get; set; }
    public District? District { get; set; }
    public Campus? Campus { get; set; }
}
