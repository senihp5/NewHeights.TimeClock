using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcCalendarEvent
{
    public long EventId { get; set; }
    public int CampusId { get; set; }
    public int? EmployeeId { get; set; }
    public CalendarEventType EventType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool AllDay { get; set; } = true;
    public string? GraphEventId { get; set; }
    public string? SharedCalendarId { get; set; }
    public string? PersonalCalendarEventId { get; set; }
    public DateTime? LastSyncDate { get; set; }
    public SyncStatus SyncStatus { get; set; } = SyncStatus.Pending;
    public string SourceType { get; set; } = "MANUAL";
    public long? SourceId { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    public Campus Campus { get; set; } = null!;
    public TcEmployee? Employee { get; set; }
}
