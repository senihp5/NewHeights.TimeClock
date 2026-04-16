using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcPayPeriodSummary
{
    public long SummaryId { get; set; }
    public int PayPeriodId { get; set; }
    public int EmployeeId { get; set; }
    public decimal TotalRegularHours { get; set; }
    public decimal TotalOvertimeHours { get; set; }
    public decimal TotalHours { get; set; }
    public int DaysWorked { get; set; }
    public int DaysAbsent { get; set; }
    public int DaysLate { get; set; }
    public int ExceptionCount { get; set; }
    public ApprovalStatus ApprovalStatus { get; set; } = ApprovalStatus.Pending;
    public string? SupervisorApprovedBy { get; set; }
    public DateTime? SupervisorApprovedDate { get; set; }
    public string? HRApprovedBy { get; set; }
    public DateTime? HRApprovedDate { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    public TcPayPeriod PayPeriod { get; set; } = null!;
    public TcEmployee Employee { get; set; } = null!;
}
