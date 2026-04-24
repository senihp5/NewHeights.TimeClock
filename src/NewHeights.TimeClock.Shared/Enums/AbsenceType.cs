namespace NewHeights.TimeClock.Shared.Enums;

/// <summary>
/// Reason a substitute is needed. Includes both "teacher absent" scenarios
/// (Vacation, PTO, Sick, Personal) AND "teacher present-but-pulled" scenarios
/// (STAARTesting, MAPTesting, TeacherMeeting, ProfessionalDev) where the
/// teacher is on-duty for something else and their class still needs coverage.
///
/// Values are stored as strings via EF HasConversion&lt;string&gt;() on the
/// TcSubRequest.AbsenceType column and surfaced as a dropdown in any UI that
/// needs to record why a sub was assigned.
/// </summary>
public enum AbsenceType
{
    Vacation,
    PTO,
    Sick,
    Personal,
    ProfessionalDev,
    STAARTesting,
    MAPTesting,
    TeacherMeeting,
    Other
}
