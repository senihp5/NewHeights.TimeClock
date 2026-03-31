namespace NewHeights.TimeClock.Data.Entities;

public class TcPunchCorrection
{
    public long CorrectionId { get; set; }

    public int? EmployeeId { get; set; }
    public string PersonIdNumber { get; set; } = string.Empty;
    public string? PersonFirstName { get; set; }
    public string? PersonLastName { get; set; }
    public int CampusId { get; set; }

    // Original punch (null = adding a punch where none existed)
    public long? OriginalPunchId { get; set; }
    public long? OriginalAttTxId { get; set; }
    public DateTime? OriginalDateTime { get; set; }
    public string? OriginalPunchType { get; set; }
    public string? OriginalSubType { get; set; }

    // The proposed correction: ADD_PUNCH, MODIFY_PUNCH, DELETE_PUNCH
    public string CorrectionType { get; set; } = string.Empty;
    public DateTime? ProposedDateTime { get; set; }
    public string? ProposedPunchType { get; set; }
    public string? ProposedSubType { get; set; }
    public string? ProposedScanType { get; set; }

    public string Reason { get; set; } = string.Empty;

    // Safety/timeclock split
    public bool SafetyImmediateApply { get; set; } = true;
    public bool AffectsTimeclock { get; set; } = false;

    // PENDING, APPROVED, REJECTED, AUTO_APPLIED
    public string Status { get; set; } = "PENDING";

    // Safety write-back
    public DateTime? SafetyAppliedDate { get; set; }
    public long? SafetyAppliedTxId { get; set; }

    // Timeclock approval
    public string? TimeclockApprovedBy { get; set; }
    public DateTime? TimeclockApprovedDate { get; set; }
    public long? TimeclockAppliedPunchId { get; set; }
    public string? TimeclockReviewNotes { get; set; }

    // Rejection
    public string? RejectedBy { get; set; }
    public DateTime? RejectedDate { get; set; }
    public string? RejectionReason { get; set; }

    // Who submitted: RECEPTION, SUPERVISOR, HR, ADMIN
    public string SubmittedByUserId { get; set; } = string.Empty;
    public string SubmittedByName { get; set; } = string.Empty;
    public string? SubmittedByRole { get; set; }
    public DateTime SubmittedDate { get; set; } = DateTime.UtcNow;

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;

    // Navigation
    public TcEmployee? Employee { get; set; }
    public Campus? Campus { get; set; }
    public TcTimePunch? OriginalPunch { get; set; }
}
