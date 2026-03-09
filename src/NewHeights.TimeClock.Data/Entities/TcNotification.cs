using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcNotification
{
    public long NotificationId { get; set; }
    public int RecipientEmployeeId { get; set; }
    public NotificationType NotificationType { get; set; }
    public NotificationChannel Channel { get; set; } = NotificationChannel.Email;
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public string? ReferenceType { get; set; }
    public long? ReferenceId { get; set; }
    public NotificationStatus Status { get; set; } = NotificationStatus.Queued;
    public DateTime? SentDate { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    public TcEmployee RecipientEmployee { get; set; } = null!;
}
