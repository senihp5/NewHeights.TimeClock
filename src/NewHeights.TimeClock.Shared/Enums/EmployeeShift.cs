namespace NewHeights.TimeClock.Shared.Enums;

/// <summary>
/// Identifies which session(s) an employee is scheduled to work.
/// Used to determine which TC_StaffHoursWindow applies when evaluating
/// early-out prompts and timecard validation.
/// </summary>
public enum EmployeeShift
{
    /// <summary>Day session only (8:20 AM – 4:15 PM, Mon–Fri).</summary>
    Day = 0,

    /// <summary>Evening session only (5:30 PM – 9:15 PM, Mon–Thu).</summary>
    Evening = 1,

    /// <summary>Both day and evening sessions (substitutes who cover either shift).</summary>
    Both = 2
}
