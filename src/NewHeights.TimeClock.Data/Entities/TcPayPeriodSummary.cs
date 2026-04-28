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

    // Migration 060 (2026-04-27): EmployeeApprovedBy + EmployeeApprovedDate
    // mirror the Supervisor/HR pattern so the paper-approval workflow can
    // record who stamped the employee submission stage and when. Audit log
    // still has the canonical trail, but having the identity on the summary
    // row itself simplifies queries + reduces joins for HR reporting.
    public string? EmployeeApprovedBy { get; set; }
    public DateTime? EmployeeApprovedDate { get; set; }
    public string? SupervisorApprovedBy { get; set; }
    public DateTime? SupervisorApprovedDate { get; set; }
    public string? HRApprovedBy { get; set; }
    public DateTime? HRApprovedDate { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    public TcPayPeriod PayPeriod { get; set; } = null!;
    public TcEmployee Employee { get; set; } = null!;
}
