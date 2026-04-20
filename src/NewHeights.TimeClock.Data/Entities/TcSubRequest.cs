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

    public TcEmployee RequestingEmployee { get; set; } = null!;
    public Campus Campus { get; set; } = null!;
    public TcSubPool? AssignedSub { get; set; }

    // Phase 5 navigation.
    public TcEmployee? AssignedSubEmployee { get; set; }
    public ICollection<TcSubOutreach> Outreach { get; set; } = new List<TcSubOutreach>();
}
