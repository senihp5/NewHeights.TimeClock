namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Migration 050: attachment row hanging off a <see cref="TcSubRequestPeriodNote"/>.
/// Typed so the same table supports paste-link URLs (today) and Azure Blob
/// Storage uploads (planned). Phase 2 will add a file-upload UI that writes
/// AttachmentType='BLOB' rows with BlobContainer + BlobKey populated; the
/// download endpoint will mint a short-lived SAS URL at request time.
/// </summary>
public class TcSubRequestPeriodAttachment
{
    public long AttachmentId { get; set; }
    public long PeriodNoteId { get; set; }

    /// <summary>
    /// "URL" = paste-link (OneDrive, Google Drive, SharePoint, etc.).
    /// "BLOB" = future Azure Blob Storage upload. Enforced by a CHECK
    /// constraint in the migration.
    /// </summary>
    public string AttachmentType { get; set; } = "URL";

    /// <summary>Human-readable display label shown in the UI.</summary>
    public string? Label { get; set; }

    /// <summary>
    /// For URL type: the raw paste-link. For BLOB type (future): cached
    /// SAS URL for direct download, regenerated periodically.
    /// </summary>
    public string? Url { get; set; }

    // ── Blob fields (future) ─────────────────────────────────────────
    public string? BlobContainer { get; set; }
    public string? BlobKey { get; set; }
    public string? ContentType { get; set; }
    public long? SizeBytes { get; set; }

    public string? UploadedBy { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.Now;

    // Navigation
    public TcSubRequestPeriodNote? PeriodNote { get; set; }
}
