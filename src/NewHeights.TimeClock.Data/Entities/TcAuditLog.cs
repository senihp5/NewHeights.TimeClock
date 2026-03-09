using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcAuditLog
{
    public long AuditId { get; set; }
    public string TableName { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;
    public AuditAction Action { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string ChangedBy { get; set; } = string.Empty;
    public string? ChangedByRole { get; set; }
    public string? IPAddress { get; set; }
    public string? Reason { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
