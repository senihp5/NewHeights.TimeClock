using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcCorrectionRequest
{
    public long RequestId { get; set; }
    public int EmployeeId { get; set; }
    public CorrectionRequestType RequestType { get; set; }
    public DateOnly WorkDate { get; set; }
    public PunchType? RequestedPunchType { get; set; }
    public DateTime? RequestedDateTime { get; set; }
    public long? OriginalPunchId { get; set; }
    public DateTime? OriginalDateTime { get; set; }
    public string Reason { get; set; } = string.Empty;
    public CorrectionRequestStatus Status { get; set; } = CorrectionRequestStatus.Submitted;
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedDate { get; set; }
    public string? ReviewNotes { get; set; }
    public long? AppliedPunchId { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;

    public TcEmployee Employee { get; set; } = null!;
    public TcTimePunch? OriginalPunch { get; set; }
    public TcTimePunch? AppliedPunch { get; set; }
}
