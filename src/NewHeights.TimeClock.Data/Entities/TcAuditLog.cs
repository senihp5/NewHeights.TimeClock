namespace NewHeights.TimeClock.Data.Entities;

public class TcAuditLog
{
    public long AuditId { get; set; }

    // Structured action code e.g. PUNCH_CREATED, CORRECTION_APPROVED, CONFIG_CHANGED
    public string ActionCode { get; set; } = string.Empty;

    // Who did it
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string? UserEmail { get; set; }
    public string? UserRole { get; set; }

    // What was affected
    // PUNCH, CORRECTION, TIMECARD, EMPLOYEE, PAY_PERIOD, BELL_SCHEDULE, CONFIG, SYSTEM
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;

    // Denormalized FK shortcuts
    public long? PunchId { get; set; }
    public long? CorrectionId { get; set; }
    public int? EmployeeId { get; set; }
    public int? CampusId { get; set; }

    // The change
    public string? OldValuesJson { get; set; }
    public string? NewValuesJson { get; set; }
    public string? DeltaSummary { get; set; }

    // Why
    public string? Reason { get; set; }

    // Where from: KIOSK, MOBILE, ADMIN_UI, RECEPTION_UI, SYSTEM, API
    public string Source { get; set; } = "SYSTEM";
    public string? IPAddress { get; set; }
    public string? SessionId { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;
}
