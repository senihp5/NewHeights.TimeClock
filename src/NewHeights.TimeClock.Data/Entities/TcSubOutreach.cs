namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// One row per sub-contacted-per-request (SubstituteTimesheetSpec.md section 15.3).
/// Supports manual single-sub outreach + auto-cascade priority queues. In the
/// auto-cascade pattern, all queued subs are written with SequenceOrder 1..N
/// but only SequenceOrder=1 gets MessageSentAt populated at send time; later
/// sends happen on prior-sub decline or token expiry.
/// </summary>
public class TcSubOutreach
{
    public long OutreachId { get; set; }
    public long SubRequestId { get; set; }
    public int SubEmployeeId { get; set; }

    /// <summary>SMS | EMAIL | BOTH. Phase 5 uses EMAIL only; Phase 6 adds SMS.</summary>
    public string OutreachMethod { get; set; } = "EMAIL";

    public string? PhoneNumber { get; set; }
    public string? EmailAddress { get; set; }

    /// <summary>Crypto-random 64-char string. Used in /sub/respond/{token} URL.</summary>
    public string ResponseToken { get; set; } = string.Empty;

    /// <summary>Default 48 hours from CreatedDate. Enforced at response time.</summary>
    public DateTime TokenExpiresAt { get; set; }

    /// <summary>NULL if queued (auto-cascade) and not yet sent.</summary>
    public DateTime? MessageSentAt { get; set; }

    /// <summary>Azure Communication Services message id (Phase 6).</summary>
    public string? MessageId { get; set; }

    /// <summary>PENDING | DELIVERED | FAILED | UNKNOWN.</summary>
    public string DeliveryStatus { get; set; } = "PENDING";

    /// <summary>AWAITING | ACCEPTED | DECLINED | EXPIRED | NO_RESPONSE.</summary>
    public string ResponseStatus { get; set; } = "AWAITING";

    public DateTime? RespondedAt { get; set; }

    /// <summary>Email of the supervisor who triggered the send.</summary>
    public string? SentBy { get; set; }

    /// <summary>1 = first sub tried, 2 = second, etc. Used for auto-cascade ordering.</summary>
    public int SequenceOrder { get; set; } = 1;

    public string? Notes { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;

    // Navigation
    public TcSubRequest? SubRequest { get; set; }
    public TcEmployee? SubEmployee { get; set; }
}
