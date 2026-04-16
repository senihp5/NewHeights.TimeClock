using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcPayPeriod
{
    public int PayPeriodId { get; set; }
    public string PeriodName { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public DateOnly? PayDate { get; set; }
    public PayPeriodStatus Status { get; set; } = PayPeriodStatus.Open;
    public string? LockedBy { get; set; }
    public DateTime? LockedDate { get; set; }
    public DateTime? ExportedDate { get; set; }
    public string? ExportedBy { get; set; }
    public string SchoolYear { get; set; } = string.Empty;
    public int PeriodNumber { get; set; }
    public DateOnly? EmployeeDeadline { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;

    public ICollection<TcPayPeriodSummary> Summaries { get; set; } = new List<TcPayPeriodSummary>();
    public ICollection<TcPayrollExport> Exports { get; set; } = new List<TcPayrollExport>();
}
