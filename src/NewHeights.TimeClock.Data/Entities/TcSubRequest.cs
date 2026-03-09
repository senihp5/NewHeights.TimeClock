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
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

    public TcEmployee RequestingEmployee { get; set; } = null!;
    public Campus Campus { get; set; } = null!;
    public TcSubPool? AssignedSub { get; set; }
}
