using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Enums;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;

namespace NewHeights.TimeClock.Web.Services;

/// <inheritdoc cref="IPayrollPdfService"/>
public class PayrollPdfService : IPayrollPdfService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly ITimesheetService _timesheetService;
    private readonly ISubstituteTimesheetService _subTimesheetService;
    private readonly ILogger<PayrollPdfService> _logger;

    // PdfSharpCore uses points (1/72") for layout. Letter portrait = 612 x 792.
    private const double PageMargin = 40;
    private const double LineHeight = 14;

    public PayrollPdfService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        ITimesheetService timesheetService,
        ISubstituteTimesheetService subTimesheetService,
        ILogger<PayrollPdfService> logger)
    {
        _dbFactory = dbFactory;
        _timesheetService = timesheetService;
        _subTimesheetService = subTimesheetService;
        _logger = logger;
    }

    public async Task<byte[]> GenerateCombinedPdfAsync(
        DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyList<int> hourlyEmployeeIds,
        IReadOnlyList<int> subEmployeeIds,
        string generatedByEmail,
        CancellationToken ct = default)
    {
        if ((hourlyEmployeeIds == null || hourlyEmployeeIds.Count == 0)
         && (subEmployeeIds == null || subEmployeeIds.Count == 0))
            throw new ArgumentException("At least one hourly or substitute employee ID required.");

        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);

        using var doc = new PdfDocument();
        doc.Info.Title = $"NH Payroll {periodStart:MMM d}-{periodEnd:MMM d, yyyy}";
        doc.Info.Author = generatedByEmail;
        doc.Info.Creator = "New Heights TimeClock — Payroll PDF Export";
        doc.Info.Subject = $"Combined timesheet bundle";

        // Cover page first.
        AddCoverPage(doc, periodStart, periodEnd, hourlyEmployeeIds.Count, subEmployeeIds.Count, generatedByEmail);

        // Hourly — one page per employee.
        foreach (var empId in hourlyEmployeeIds ?? Array.Empty<int>())
        {
            try
            {
                await AddHourlyTimesheetPageAsync(doc, ctx, empId, periodStart, periodEnd, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF: failed to render hourly employee {Id}", empId);
            }
        }

        // Subs — one page per employee summarizing all their timecards.
        foreach (var empId in subEmployeeIds ?? Array.Empty<int>())
        {
            try
            {
                await AddSubstituteTimesheetPageAsync(doc, ctx, empId, periodStart, periodEnd, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "PDF: failed to render substitute employee {Id}", empId);
            }
        }

        // Roster summary at the end.
        AddRosterSummaryPage(doc, periodStart, periodEnd, hourlyEmployeeIds, subEmployeeIds);

        using var ms = new MemoryStream();
        doc.Save(ms, false);
        return ms.ToArray();
    }

    // ── Cover ────────────────────────────────────────────────────────────

    private static void AddCoverPage(
        PdfDocument doc, DateOnly periodStart, DateOnly periodEnd,
        int hourlyCount, int subCount, string generatedByEmail)
    {
        var page = doc.AddPage();
        using var gfx = XGraphics.FromPdfPage(page);
        var titleFont = new XFont("Arial", 22, XFontStyle.Bold);
        var subFont = new XFont("Arial", 12, XFontStyle.Regular);
        var smallFont = new XFont("Arial", 10, XFontStyle.Regular);

        var navy = XBrushes.Navy;
        gfx.DrawString("New Heights — Payroll Export", titleFont, navy, new XRect(PageMargin, 80, page.Width - 2 * PageMargin, 30), XStringFormats.TopLeft);

        var dates = $"{periodStart:dddd, MMMM d} – {periodEnd:dddd, MMMM d, yyyy}";
        gfx.DrawString(dates, subFont, XBrushes.Black, new XRect(PageMargin, 120, page.Width - 2 * PageMargin, 18), XStringFormats.TopLeft);

        var y = 170.0;
        gfx.DrawString("Bundle contents", subFont, navy, new XRect(PageMargin, y, 400, 18), XStringFormats.TopLeft);
        y += 22;
        gfx.DrawString($"  · Hourly employees:    {hourlyCount}", smallFont, XBrushes.Black, new XRect(PageMargin, y, 400, 16), XStringFormats.TopLeft); y += 16;
        gfx.DrawString($"  · Substitute employees: {subCount}", smallFont, XBrushes.Black, new XRect(PageMargin, y, 400, 16), XStringFormats.TopLeft); y += 32;

        gfx.DrawString($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm} by {generatedByEmail}", smallFont, XBrushes.DimGray,
            new XRect(PageMargin, y, page.Width - 2 * PageMargin, 16), XStringFormats.TopLeft);
    }

    // ── Hourly per-employee page ────────────────────────────────────────

    private async Task AddHourlyTimesheetPageAsync(
        PdfDocument doc, TimeClockDbContext ctx,
        int employeeId, DateOnly periodStart, DateOnly periodEnd, CancellationToken ct)
    {
        var employee = await ctx.TcEmployees
            .AsNoTracking()
            .Include(e => e.Staff)
            .Include(e => e.Supervisor).ThenInclude(s => s!.Staff)
            .Include(e => e.HomeCampus)
            .FirstOrDefaultAsync(e => e.EmployeeId == employeeId, ct);
        if (employee == null) return;

        var timesheet = await _timesheetService.GetPayPeriodTimesheetAsync(employeeId, periodStart, periodEnd);

        var page = doc.AddPage();
        using var gfx = XGraphics.FromPdfPage(page);
        var headerFont = new XFont("Arial", 14, XFontStyle.Bold);
        var labelFont = new XFont("Arial", 9, XFontStyle.Bold);
        var bodyFont = new XFont("Arial", 9, XFontStyle.Regular);
        var smallFont = new XFont("Arial", 8, XFontStyle.Regular);

        var width = page.Width - 2 * PageMargin;
        var x = PageMargin;
        var y = PageMargin;

        // Title.
        var fullName = employee.Staff?.FullName ?? employee.DisplayName ?? employee.Email ?? $"Employee {employeeId}";
        gfx.DrawString($"Hourly Timesheet — {fullName}", headerFont, XBrushes.Navy,
            new XRect(x, y, width, 18), XStringFormats.TopLeft);
        y += 22;

        // Period strip.
        gfx.DrawString($"Pay period: {periodStart:MMM d} – {periodEnd:MMM d, yyyy}    ·    ID: {employee.IdNumber}    ·    Campus: {employee.HomeCampus?.CampusName ?? "—"}    ·    Supervisor: {employee.Supervisor?.Staff?.FullName ?? "—"}",
            smallFont, XBrushes.DimGray, new XRect(x, y, width, 14), XStringFormats.TopLeft);
        y += 20;

        // Day-by-day table header.
        var col1 = x;
        var col2 = x + 110;
        var col3 = x + 200;
        var col4 = x + 270;
        var col5 = x + 340;
        var col6 = x + 410;
        var col7 = x + 480;

        gfx.DrawRectangle(XBrushes.LightGray, x, y, width, 16);
        gfx.DrawString("Date", labelFont, XBrushes.Black, new XRect(col1 + 4, y + 2, 100, 14), XStringFormats.TopLeft);
        gfx.DrawString("In / Out", labelFont, XBrushes.Black, new XRect(col2 + 4, y + 2, 90, 14), XStringFormats.TopLeft);
        gfx.DrawString("Worked", labelFont, XBrushes.Black, new XRect(col3 + 4, y + 2, 70, 14), XStringFormats.TopLeft);
        gfx.DrawString("Leave", labelFont, XBrushes.Black, new XRect(col4 + 4, y + 2, 70, 14), XStringFormats.TopLeft);
        gfx.DrawString("Holiday", labelFont, XBrushes.Black, new XRect(col5 + 4, y + 2, 70, 14), XStringFormats.TopLeft);
        gfx.DrawString("Total", labelFont, XBrushes.Black, new XRect(col6 + 4, y + 2, 70, 14), XStringFormats.TopLeft);
        gfx.DrawString("Note", labelFont, XBrushes.Black, new XRect(col7 + 4, y + 2, 90, 14), XStringFormats.TopLeft);
        y += 18;

        // PayPeriodTimesheet groups days by week — flatten into a single
        // chronological day list for the PDF row-by-row render.
        var allDays = timesheet.Weeks.SelectMany(w => w.Days).OrderBy(d => d.Date);
        foreach (var day in allDays)
        {
            if (y > page.Height - PageMargin - 80)
            {
                // Out of room — overflow to next page.
                page = doc.AddPage();
                using var gfx2 = XGraphics.FromPdfPage(page);
                y = PageMargin;
                continue;
            }

            gfx.DrawString(day.Date.ToString("ddd M/d"), bodyFont, XBrushes.Black, new XRect(col1 + 4, y, 100, 14), XStringFormats.TopLeft);

            var inOut = (day.FirstIn != null ? day.FirstIn.PunchTime.ToString("h:mm tt") : "—")
                      + " – "
                      + (day.LastOut != null ? day.LastOut.PunchTime.ToString("h:mm tt") : "—");
            gfx.DrawString(inOut, bodyFont, XBrushes.Black, new XRect(col2 + 4, y, 90, 14), XStringFormats.TopLeft);

            gfx.DrawString(day.TotalHours.ToString("F2"), bodyFont, XBrushes.Black, new XRect(col3 + 4, y, 70, 14), XStringFormats.TopLeft);
            gfx.DrawString("0.00", bodyFont, XBrushes.Black, new XRect(col4 + 4, y, 70, 14), XStringFormats.TopLeft);
            gfx.DrawString("0.00", bodyFont, XBrushes.Black, new XRect(col5 + 4, y, 70, 14), XStringFormats.TopLeft);
            gfx.DrawString(day.TotalHours.ToString("F2"), bodyFont, XBrushes.Black, new XRect(col6 + 4, y, 70, 14), XStringFormats.TopLeft);

            var note = day.HasException ? "EXC" : (day.ShortDayReason ?? "");
            gfx.DrawString(note, smallFont, XBrushes.DimGray, new XRect(col7 + 4, y, 90, 14), XStringFormats.TopLeft);
            y += LineHeight;
        }

        // Totals.
        y += 6;
        gfx.DrawLine(XPens.Black, x, y, x + width, y);
        y += 6;
        gfx.DrawString($"Period totals: Worked {timesheet.TotalRegularHours:F2}    OT {timesheet.TotalOvertimeHours:F2}    Total {timesheet.TotalHours:F2}    Days {timesheet.DaysWorked}",
            labelFont, XBrushes.Navy, new XRect(x, y, width, 14), XStringFormats.TopLeft);
        y += 24;

        // Approval audit trail.
        gfx.DrawString("Approval audit trail", labelFont, XBrushes.Navy, new XRect(x, y, width, 14), XStringFormats.TopLeft);
        y += 16;

        var summary = await ctx.TcPayPeriodSummaries
            .AsNoTracking()
            .Include(s => s.PayPeriod)
            .FirstOrDefaultAsync(s => s.EmployeeId == employeeId
                                   && s.PayPeriod.StartDate == periodStart
                                   && s.PayPeriod.EndDate == periodEnd, ct);

        gfx.DrawString($"Employee submission:  {(summary?.EmployeeApprovedBy ?? "(not stamped)")}    {(summary?.EmployeeApprovedDate?.ToString("yyyy-MM-dd HH:mm") ?? "")}",
            bodyFont, XBrushes.Black, new XRect(x, y, width, 14), XStringFormats.TopLeft);
        y += LineHeight;
        gfx.DrawString($"Supervisor approval: {(summary?.SupervisorApprovedBy ?? "(not stamped)")}    {(summary?.SupervisorApprovedDate?.ToString("yyyy-MM-dd HH:mm") ?? "")}",
            bodyFont, XBrushes.Black, new XRect(x, y, width, 14), XStringFormats.TopLeft);
        y += LineHeight;
        gfx.DrawString($"HR approval:         {(summary?.HRApprovedBy ?? "(not stamped)")}    {(summary?.HRApprovedDate?.ToString("yyyy-MM-dd HH:mm") ?? "")}",
            bodyFont, XBrushes.Black, new XRect(x, y, width, 14), XStringFormats.TopLeft);
    }

    // ── Substitute per-employee page ────────────────────────────────────

    private async Task AddSubstituteTimesheetPageAsync(
        PdfDocument doc, TimeClockDbContext ctx,
        int employeeId, DateOnly periodStart, DateOnly periodEnd, CancellationToken ct)
    {
        var employee = await ctx.TcEmployees
            .AsNoTracking()
            .Include(e => e.Staff)
            .FirstOrDefaultAsync(e => e.EmployeeId == employeeId, ct);
        if (employee == null) return;

        var cards = await ctx.TcSubstituteTimecards
            .AsNoTracking()
            .Include(t => t.Campus)
            .Include(t => t.PeriodEntries)
            .Where(t => t.EmployeeId == employeeId
                     && t.WorkDate >= periodStart
                     && t.WorkDate <= periodEnd)
            .OrderBy(t => t.WorkDate)
            .ToListAsync(ct);

        var page = doc.AddPage();
        using var gfx = XGraphics.FromPdfPage(page);
        var headerFont = new XFont("Arial", 14, XFontStyle.Bold);
        var labelFont = new XFont("Arial", 9, XFontStyle.Bold);
        var bodyFont = new XFont("Arial", 9, XFontStyle.Regular);
        var smallFont = new XFont("Arial", 8, XFontStyle.Regular);

        var width = page.Width - 2 * PageMargin;
        var x = PageMargin;
        var y = PageMargin;

        var fullName = employee.Staff?.FullName ?? employee.DisplayName ?? employee.Email ?? $"Employee {employeeId}";
        gfx.DrawString($"Substitute Timesheet — {fullName}", headerFont, XBrushes.Navy,
            new XRect(x, y, width, 18), XStringFormats.TopLeft);
        y += 22;
        gfx.DrawString($"Pay period: {periodStart:MMM d} – {periodEnd:MMM d, yyyy}    ·    ID: {employee.IdNumber}    ·    Cards: {cards.Count}",
            smallFont, XBrushes.DimGray, new XRect(x, y, width, 14), XStringFormats.TopLeft);
        y += 20;

        gfx.DrawRectangle(XBrushes.LightGray, x, y, width, 16);
        gfx.DrawString("Date", labelFont, XBrushes.Black, new XRect(x + 4, y + 2, 70, 14), XStringFormats.TopLeft);
        gfx.DrawString("Campus", labelFont, XBrushes.Black, new XRect(x + 80, y + 2, 90, 14), XStringFormats.TopLeft);
        gfx.DrawString("Period", labelFont, XBrushes.Black, new XRect(x + 180, y + 2, 50, 14), XStringFormats.TopLeft);
        gfx.DrawString("Teacher Covered", labelFont, XBrushes.Black, new XRect(x + 230, y + 2, 130, 14), XStringFormats.TopLeft);
        gfx.DrawString("Subject", labelFont, XBrushes.Black, new XRect(x + 365, y + 2, 90, 14), XStringFormats.TopLeft);
        gfx.DrawString("Status", labelFont, XBrushes.Black, new XRect(x + 460, y + 2, 80, 14), XStringFormats.TopLeft);
        y += 18;

        var totalPeriods = 0;
        foreach (var card in cards)
        {
            foreach (var entry in card.PeriodEntries.OrderBy(e => e.PeriodNumber))
            {
                if (y > page.Height - PageMargin - 60)
                {
                    page = doc.AddPage();
                    using var gfx2 = XGraphics.FromPdfPage(page);
                    y = PageMargin;
                }
                gfx.DrawString(card.WorkDate.ToString("M/d"), bodyFont, XBrushes.Black, new XRect(x + 4, y, 70, 14), XStringFormats.TopLeft);
                gfx.DrawString(card.Campus?.CampusName ?? "—", bodyFont, XBrushes.Black, new XRect(x + 80, y, 90, 14), XStringFormats.TopLeft);
                gfx.DrawString($"P{entry.PeriodNumber}", bodyFont, XBrushes.Black, new XRect(x + 180, y, 50, 14), XStringFormats.TopLeft);
                var teacherDisplay = !string.IsNullOrWhiteSpace(entry.TeacherReplaced) ? entry.TeacherReplaced! : "—";
                if (teacherDisplay.Length > 22) teacherDisplay = teacherDisplay.Substring(0, 21) + "…";
                gfx.DrawString(teacherDisplay, bodyFont, XBrushes.Black, new XRect(x + 230, y, 130, 14), XStringFormats.TopLeft);
                var subj = entry.ContentArea ?? entry.CourseName ?? "—";
                if (subj.Length > 16) subj = subj.Substring(0, 15) + "…";
                gfx.DrawString(subj, bodyFont, XBrushes.Black, new XRect(x + 365, y, 90, 14), XStringFormats.TopLeft);
                gfx.DrawString(card.ApprovalStatus.ToString(), smallFont, XBrushes.DimGray, new XRect(x + 460, y, 80, 14), XStringFormats.TopLeft);
                y += LineHeight;
                totalPeriods++;
            }
        }

        y += 8;
        gfx.DrawLine(XPens.Black, x, y, x + width, y);
        y += 6;
        gfx.DrawString($"Period totals: {totalPeriods} periods covered across {cards.Count} timecard(s)",
            labelFont, XBrushes.Navy, new XRect(x, y, width, 14), XStringFormats.TopLeft);
    }

    // ── Roster summary page ─────────────────────────────────────────────

    private static void AddRosterSummaryPage(
        PdfDocument doc, DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyList<int> hourlyIds, IReadOnlyList<int> subIds)
    {
        var page = doc.AddPage();
        using var gfx = XGraphics.FromPdfPage(page);
        var headerFont = new XFont("Arial", 14, XFontStyle.Bold);
        var bodyFont = new XFont("Arial", 9, XFontStyle.Regular);

        var width = page.Width - 2 * PageMargin;
        var x = PageMargin;
        var y = PageMargin;

        gfx.DrawString("Roster Summary", headerFont, XBrushes.Navy, new XRect(x, y, width, 18), XStringFormats.TopLeft);
        y += 22;
        gfx.DrawString($"Pay period: {periodStart:MMM d} – {periodEnd:MMM d, yyyy}", bodyFont, XBrushes.Black, new XRect(x, y, width, 14), XStringFormats.TopLeft);
        y += 24;
        gfx.DrawString($"This bundle includes {hourlyIds.Count} hourly timesheet(s) and {subIds.Count} substitute timesheet(s).",
            bodyFont, XBrushes.Black, new XRect(x, y, width, 14), XStringFormats.TopLeft);
        y += 18;
        gfx.DrawString("See individual pages above for per-employee detail and approval audit trails.",
            bodyFont, XBrushes.DimGray, new XRect(x, y, width, 14), XStringFormats.TopLeft);
    }
}
