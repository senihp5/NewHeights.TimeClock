namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Generates a combined PDF (one timesheet per page) covering selected
/// hourly + substitute employees in a pay period. Used by the HR Payroll
/// Review "Approve Selected and Export PDF" workflow shipped 2026-04-27.
///
/// Output is a single byte array suitable for streaming to the browser
/// for download AND for archival into TC_PayrollExport.PdfBlob.
/// </summary>
public interface IPayrollPdfService
{
    /// <summary>
    /// Build the PDF. Hourly + sub IDs may be empty individually but at
    /// least one ID across both lists must be supplied. Layout: header
    /// page, one page per hourly timesheet, one page per substitute
    /// summary, then a roster summary page at the end.
    /// </summary>
    Task<byte[]> GenerateCombinedPdfAsync(
        DateOnly periodStart,
        DateOnly periodEnd,
        IReadOnlyList<int> hourlyEmployeeIds,
        IReadOnlyList<int> subEmployeeIds,
        string generatedByEmail,
        CancellationToken ct = default);
}
