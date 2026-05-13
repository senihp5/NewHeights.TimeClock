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
    // Phase A extends this: AwaitingSub -> PartiallyAssigned -> SubConfirmed.
    AwaitingSub,

    AbsenceApproved,
    SubAssigned,
    SubConfirmed,
    Denied,
    Cancelled,

    // Phase A (migration 048): one or more subs have accepted a subset of the
    // requested periods but not all. Outreach cascade continues for the
    // remaining uncovered periods. Transitions to SubConfirmed when the final
    // period gets claimed.
    PartiallyAssigned,

    // 2026-04-28: every queued sub has declined / expired without an accept.
    // Teacher (or supervisor) can now pick a fellow teacher as a one-off
    // sub via SubOutreachPanel's teacher-fallback picker, which creates a
    // single TcSubOutreach row with the teacher's EmployeeId and reuses the
    // existing accept/decline email flow. If the teacher-sub declines, the
    // request returns to AllSubsExhausted so another teacher can be picked.
    AllSubsExhausted
}
