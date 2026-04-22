using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcSubRequest
{
    public long SubRequestId { get; set; }
    public int RequestingEmployeeId { get; set; }
    public int CampusId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public AbsenceType AbsenceType { get; set; }
    public string? AbsenceReason { get; set; }
    public string? PeriodsNeeded { get; set; }
    public string? SubjectArea { get; set; }
    public string? SpecialInstructions { get; set; }
    public int? AssignedSubPoolId { get; set; }
    public DateTime? AssignedDate { get; set; }
    public string? AssignedBy { get; set; }
    public SubRequestStatus Status { get; set; } = SubRequestStatus.Submitted;
    public string? SupervisorApprovedBy { get; set; }
    public DateTime? SupervisorApprovedDate { get; set; }
    public string? CalendarEventId { get; set; }
    public bool IsCalendarSynced { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    // Phase 5 scheduling columns (migration 035a).
    /// <summary>DAY | EVENING | BOTH. Determines which bell schedule applies.</summary>
    public string? SessionType { get; set; }

    /// <summary>Populated when a sub accepts the outreach. FK → TcEmployee.</summary>
    public int? AssignedSubEmployeeId { get; set; }

    /// <summary>Stamped when the accept-confirmation email fires.</summary>
    public DateTime? ConfirmationSentAt { get; set; }

    /// <summary>
    /// Phase 9c: last time SubRequestEscalationService notified the campus
    /// admin that this AwaitingSub request has been sitting without outreach.
    /// Used to throttle re-escalations within ReEscalationIntervalHours.
    /// </summary>
    public DateTime? EscalatedAt { get; set; }

    /// <summary>
    /// Migration 042: when a supervisor or campus admin submits the absence on
    /// behalf of the absentee, this column carries the actor's EmployeeId.
    /// NULL for self-submitted requests (the common case).
    /// RequestingEmployeeId remains the absentee regardless.
    /// </summary>
    public int? CreatedByEmployeeId { get; set; }

    /// <summary>
    /// Migration 044 (Phase D4B): sub pool scope. Values:
    ///   TEACHER    — draws from subs with SubRole TEACHER_SUB (or NULL legacy)
    ///   RECEPTION  — draws from subs with SubRole ADMIN_SUB only
    /// NULL is treated as TEACHER by application code for backward compat
    /// with requests created before this column existed.
    /// </summary>
    public string? RequestType { get; set; }

    /// <summary>
    /// Phase A (migration 048): dedup timestamp for the hourly background
    /// service that notifies a supervisor when a request stays
    /// PartiallyAssigned past the stall threshold. NULL means "has not yet
    /// been alerted for this partial-stall window."
    /// </summary>
    public DateTime? PartialStallAlertSentAt { get; set; }

    /// <summary>
    /// Phase A ext (migration 049): Emergency Fill flag. True means same-day
    /// must-fill — outreach broadcasts to every candidate simultaneously
    /// (no sequential cascade), tokens expire in 30 minutes instead of the
    /// default 2h, and the email/UI carry an URGENT banner. Only
    /// supervisors/admins/campus-admins can set this on submit.
    /// </summary>
    public bool IsEmergency { get; set; }

    public TcEmployee RequestingEmployee { get; set; } = null!;
    public Campus Campus { get; set; } = null!;
    public TcSubPool? AssignedSub { get; set; }

    // Phase 5 navigation.
    public TcEmployee? AssignedSubEmployee { get; set; }

    // Migration 042 navigation.
    public TcEmployee? CreatedByEmployee { get; set; }

    public ICollection<TcSubOutreach> Outreach { get; set; } = new List<TcSubOutreach>();

    // Phase A (migration 048): partial-acceptance join. Authoritative for
    // "who covers which periods" on this request.
    public ICollection<TcSubRequestAssignment> Assignments { get; set; } = new List<TcSubRequestAssignment>();

    // Migration 050: per-period class notes + attachments (URLs now, Azure
    // Blob uploads later). One TcSubRequestPeriodNote per covered period.
    public ICollection<TcSubRequestPeriodNote> PeriodNotes { get; set; } = new List<TcSubRequestPeriodNote>();
}
