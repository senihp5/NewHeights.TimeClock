namespace NewHeights.TimeClock.Shared.Audit;

/// <summary>
/// DTO describing a single audited action before it is persisted to TC_AuditLog.
/// All fields except the four required ones (ActionCode, EntityType, EntityId, Source)
/// are optional — the AuditService fills user context automatically when the calling
/// code runs inside a user session.
/// </summary>
public class AuditEntry
{
    // What happened (required). Use constants from AuditActions.
    public required string ActionCode { get; set; }

    // What was affected (required). Use constants from AuditEntityTypes.
    public required string EntityType { get; set; }
    public required string EntityId { get; set; }

    // Who did it — auto-populated from IUserContextService when null.
    public string? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public string? UserRole { get; set; }

    // Denormalized FK shortcuts for fast per-subject lookups.
    public int? EmployeeId { get; set; }
    public int? CampusId { get; set; }
    public long? PunchId { get; set; }
    public long? CorrectionId { get; set; }

    // The change. AuditService serializes to JSON if the value is an object.
    public object? OldValues { get; set; }
    public object? NewValues { get; set; }
    public string? DeltaSummary { get; set; }

    // Why (optional free-text).
    public string? Reason { get; set; }

    // Where from — use constants from AuditSource.
    public string Source { get; set; } = AuditSource.System;

    // Auto-populated from HttpContext when null.
    public string? IpAddress { get; set; }
    public string? SessionId { get; set; }
}
