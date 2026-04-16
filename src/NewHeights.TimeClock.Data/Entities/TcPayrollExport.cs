using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

public class TcPayrollExport
{
    public long ExportId { get; set; }
    public int PayPeriodId { get; set; }
    public string ExportFormat { get; set; } = "CSV";
    public string ExportMethod { get; set; } = "FILE";
    public string? FileName { get; set; }
    public int? RecordCount { get; set; }
    public decimal? TotalRegularHours { get; set; }
    public decimal? TotalOvertimeHours { get; set; }
    public ExportStatus Status { get; set; } = ExportStatus.Generated;
    public string? ErrorLog { get; set; }
    public string ExportedBy { get; set; } = string.Empty;
    public DateTime ExportDate { get; set; } = DateTime.Now;

    // Alias for compatibility
    public DateTime ExportedDate 
    { 
        get => ExportDate; 
        set => ExportDate = value; 
    }

    public TcPayPeriod PayPeriod { get; set; } = null!;
}

