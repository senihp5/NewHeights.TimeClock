namespace NewHeights.TimeClock.Shared.Enums;

/// <summary>
/// Lifecycle status for a TC_ClassAttendanceSheet row, mirroring the
/// existing employee→supervisor→HR approval chain pattern used in the
/// substitute timesheet system (one tier shorter: teacher→reception).
///
/// State machine:
///   NotStarted  - sheet row exists but no rows marked yet
///                 (lazy-created row, or background-seeded)
///   InProgress  - teacher is actively marking; not yet submitted
///   Submitted   - teacher hit Submit; awaiting receptionist validation
///   Rejected    - receptionist returned the sheet with a reason;
///                 teacher edits and re-submits (TeacherResubmitCount++)
///   Validated   - receptionist confirmed PS matches the sheet
///                 (Validation IS the PS reconciliation marker - no
///                 separate "transferred to PS" flag)
///   Reopened    - CampusAdmin+ unlocked a previously Validated sheet
///                 for correction; returns to InProgress on next edit
///
/// Stored as a string in SQL via HasConversion&lt;string&gt;().
/// </summary>
public enum ClassAttendanceSheetStatus
{
    NotStarted,
    InProgress,
    Submitted,
    Rejected,
    Validated,
    Reopened
}
