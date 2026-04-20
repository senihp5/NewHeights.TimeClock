namespace NewHeights.TimeClock.Shared.Enums;

public enum SubRequestStatus
{
    // Legacy initial state (pre-Phase 9a). Kept for backward compatibility with
    // any rows that still exist in the old supervisor-first flow. New requests
    // created via /employee/absence-request use AwaitingSub instead.
    Submitted,

    // Phase 9a (2026-04-20): teacher-driven flow. New requests start here so
    // the teacher owns sub-finding before the request reaches admin approval.
    // Lifecycle: AwaitingSub -> SubConfirmed -> AbsenceApproved (final).
    AwaitingSub,

    AbsenceApproved,
    SubAssigned,
    SubConfirmed,
    Denied,
    Cancelled
}
