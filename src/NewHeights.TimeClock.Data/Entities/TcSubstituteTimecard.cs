using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// One row per substitute per campus per day. Replaces TcDailyTimecard for
/// EmployeeType.Substitute. Subs are paid per period, not per hour. The campus
/// on this row determines which supervisor approves it (campus-scoped routing),
/// so a sub who works both campuses in one day produces two rows.
/// </summary>
public class TcSubstituteTimecard
{
    public long SubTimecardId { get; set; }

    public int EmployeeId { get; set; }
    public int CampusId { get; set; }
    public DateOnly WorkDate { get; set; }

    // Linked campus-arrival / campus-departure punches (TcTimePunch audit trail).
    public long? CheckInPunchId { get; set; }
    public long? CheckOutPunchId { get; set; }

    // Denormalized count of period entries for quick queries.
    public int TotalPeriodsWorked { get; set; }

    // Stored as INT in the DB per spec; maps 1:1 to ApprovalStatus enum ordinal.
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedDate { get; set; }

    public string? Notes { get; set; }

    // Local time per Critical Rule 4.1 — never DateTime.UtcNow.
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    // Navigation
    public TcEmployee? Employee { get; set; }
    public Campus? Campus { get; set; }
    public TcTimePunch? CheckInPunch { get; set; }
    public TcTimePunch? CheckOutPunch { get; set; }
    public ICollection<TcSubstitutePeriodEntry> PeriodEntries { get; set; } = new List<TcSubstitutePeriodEntry>();
}
