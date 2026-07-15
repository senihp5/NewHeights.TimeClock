namespace NewHeights.TimeClock.Shared.Enums;

/// <summary>
/// Per-cell attendance state for a student in a class section on a date.
///
/// Maps to PowerSchool's per-meeting attendance code dropdown via the
/// PERSISTED computed column TC_ClassAttendance.PsAttendanceCode:
///   Present  -> ''   (blank, the PS default)
///   Tardy    -> 'T'
///   Absent   -> 'UA' (Unexcused Absent - the only absence code NH uses)
///   Excused  -> 'UA' (PS dropdown has no E code; reception notes excuse in Comment)
///   EarlyOut -> ''   (PS has no early-out code; flagged on archive PDF only)
///
/// Stored as a string in SQL via HasConversion&lt;string&gt;() per the
/// reference_enum_columns_stored_as_strings convention.
/// </summary>
public enum ClassAttendanceStatus
{
    Present,
    Tardy,
    Absent,
    Excused,
    EarlyOut
}
