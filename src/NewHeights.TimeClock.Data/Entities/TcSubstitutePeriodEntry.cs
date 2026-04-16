namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// One row per class period the sub actually taught. This is the BILLABLE unit
/// — any presence for a period counts as one full period (partial periods are
/// not tracked; see spec section 14). Denormalized teacher/course/room fields
/// preserve the historical record if the master schedule is later edited.
/// </summary>
public class TcSubstitutePeriodEntry
{
    public long EntryId { get; set; }

    public long SubTimecardId { get; set; }
    public int BellPeriodId { get; set; }
    public int? MasterScheduleId { get; set; }
    public long? SubRequestId { get; set; }

    public int PeriodNumber { get; set; }
    public string PeriodName { get; set; } = string.Empty;
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }

    // Denormalized snapshot of the class being covered.
    public string? TeacherReplaced { get; set; }
    public string? CourseName { get; set; }
    public string? ContentArea { get; set; }
    public string? Room { get; set; }

    // DAY or NIGHT — filters the period picker by session.
    public string SessionType { get; set; } = "DAY";

    // MANUAL, PRE_ASSIGNED, AUTO_INFERRED — how the entry was created.
    public string EntrySource { get; set; } = "MANUAL";

    // Supervisor verification flag (independent of overall approval).
    public bool IsVerified { get; set; }

    public string? Notes { get; set; }

    // Local time per Critical Rule 4.1.
    public DateTime CreatedDate { get; set; } = DateTime.Now;

    // Navigation
    public TcSubstituteTimecard? SubTimecard { get; set; }
    public TcBellPeriod? BellPeriod { get; set; }
    public TcMasterSchedule? MasterScheduleEntry { get; set; }
    public TcSubRequest? SubRequest { get; set; }
}
