namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Phase A (migration 048): a partial-acceptance assignment of a TcSubRequest
/// to a single sub. The join table is authoritative for "who covers which
/// periods" on a request. Sum of PeriodsCovered across all assignment rows
/// for a request, when equal to the request's original PeriodsNeeded, means
/// the request is fully covered.
///
/// Legacy TcSubRequest.AssignedSubEmployeeId is preserved for backward compat
/// with pre-Phase-A rows but is no longer written by new partial accepts.
/// Display/query code looking for "the primary sub" should pick the first
/// assignment by AcceptedAt ascending.
/// </summary>
public class TcSubRequestAssignment
{
    public long AssignmentId { get; set; }
    public long SubRequestId { get; set; }
    public int SubEmployeeId { get; set; }

    /// <summary>
    /// CSV list of period identifiers this sub committed to, drawn from the
    /// parent request's PeriodsNeeded (e.g. "P1,P3"). Order-independent;
    /// callers should parse + canonicalize on read.
    /// </summary>
    public string PeriodsCovered { get; set; } = string.Empty;

    public DateTime AcceptedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// Supervisor email when a supervisor manually assigns rather than the
    /// sub accepting via their own token. NULL for self-accepts via
    /// /sub/respond/{token}.
    /// </summary>
    public string? AssignedBy { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.Now;

    // Navigation
    public TcSubRequest? SubRequest { get; set; }
    public TcEmployee? SubEmployee { get; set; }
}
