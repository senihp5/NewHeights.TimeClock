namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Migration 050: per-period class notes written by the requesting teacher
/// so the substitute (and supervisor) can see exactly what needs to happen
/// during each covered period. One row per (SubRequestId, PeriodIdentifier)
/// — enforced by a unique index on the pair — so a teacher can have
/// completely different notes for P1 vs. P3.
///
/// Attachments are stored separately in <see cref="TcSubRequestPeriodAttachment"/>
/// and can be URLs today (cloud drive paste-links) or Azure Blob references
/// once upload support lands.
/// </summary>
public class TcSubRequestPeriodNote
{
    public long PeriodNoteId { get; set; }
    public long SubRequestId { get; set; }

    /// <summary>
    /// Period label pulled from the parent request's PeriodsNeeded CSV
    /// (e.g. "P1", "P2"). Canonicalized uppercase + trimmed. Matches the
    /// format used by TcSubRequestAssignment.PeriodsCovered.
    /// </summary>
    public string PeriodIdentifier { get; set; } = string.Empty;

    public string? Notes { get; set; }

    public string? CreatedBy { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public string? ModifiedBy { get; set; }
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    // Navigation
    public TcSubRequest? SubRequest { get; set; }
    public ICollection<TcSubRequestPeriodAttachment> Attachments { get; set; }
        = new List<TcSubRequestPeriodAttachment>();
}
