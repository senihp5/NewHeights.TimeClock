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

    /// <summary>
    /// Migration 060 (2026-04-27): raw bytes of the CSV file generated at
    /// export time. NULL for legacy rows pre-migration; populated for every
    /// new export. Useful when HR needs to recover the exact file sent to
    /// Ascender even after rows in TC_DailyTimecards have been edited.
    /// </summary>
    public byte[]? CsvBlob { get; set; }

    /// <summary>
    /// Migration 060: raw bytes of the combined PDF (one timesheet per page)
    /// generated alongside the CSV at export time. Same recovery rationale.
    /// </summary>
    public byte[]? PdfBlob { get; set; }

    // Alias for compatibility
    public DateTime ExportedDate 
    { 
        get => ExportDate; 
        set => ExportDate = value; 
    }

    public TcPayPeriod PayPeriod { get; set; } = null!;
}

