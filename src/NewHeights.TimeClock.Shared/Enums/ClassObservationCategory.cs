namespace NewHeights.TimeClock.Shared.Enums;

/// <summary>
/// Classification for a teacher's per-student journal entry on
/// TC_ClassObservation. Drives filter / report rollups; never
/// transcribed to PowerSchool.
///
///   Behavior  - disruptions, conflicts, positive behavior callouts
///   Health    - nurse visits, medication, observed symptoms
///   Academic  - work completion, effort, engagement, concerns
///   Other     - anything else worth flagging for reception / admin
///
/// Stored as a string in SQL via HasConversion&lt;string&gt;() per the
/// reference_enum_columns_stored_as_strings convention. Extend by
/// adding a value here AND updating the CK_TC_ClassObservation_Category
/// CHECK constraint in a migration.
/// </summary>
public enum ClassObservationCategory
{
    Behavior,
    Health,
    Academic,
    Other
}
