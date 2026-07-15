using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Cell-level attendance writes + sheet workflow transitions. Local-only;
/// no cross-DB reads needed.
/// </summary>
public class ClassAttendanceService : IClassAttendanceService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly ILogger<ClassAttendanceService> _logger;

    public ClassAttendanceService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        ILogger<ClassAttendanceService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<TcClassAttendanceSheet> EnsureSheetAsync(
        int classSectionId, DateOnly date, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await EnsureSheetInternalAsync(db, classSectionId, date, ct);
    }

    public async Task<List<TcClassAttendance>> GetCellsForSectionDateAsync(
        int classSectionId, DateOnly date, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TcClassAttendances
            .AsNoTracking()
            .Where(a => a.ClassSectionId == classSectionId && a.AttendanceDate == date)
            .ToListAsync(ct);
    }

    public async Task MarkAsync(
        int classSectionId,
        int studentDcid,
        string studentNumber,
        DateOnly date,
        ClassAttendanceStatus status,
        string? comment,
        ClassAttendanceSource source,
        DateTime? scannedAt,
        int? minutesLate,
        string markedBy,
        CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);

        var section = await db.TcClassSections
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ClassSectionId == classSectionId, ct);
        if (section == null)
            throw new InvalidOperationException($"ClassSectionId {classSectionId} not found.");

        var sheet = await EnsureSheetInternalAsync(db, classSectionId, date, ct);

        var existing = await db.TcClassAttendances
            .FirstOrDefaultAsync(a => a.ClassSectionId == classSectionId
                                   && a.StudentDcid == studentDcid
                                   && a.AttendanceDate == date, ct);

        if (existing == null)
        {
            db.TcClassAttendances.Add(new TcClassAttendance
            {
                ClassSectionId = classSectionId,
                StudentDcid = studentDcid,
                StudentNumber = studentNumber,
                AttendanceDate = date,
                DistrictId = section.DistrictId,
                CampusId = section.CampusId,
                Status = status,
                Comment = comment,
                ScannedAt = scannedAt,
                MinutesLate = minutesLate,
                Source = source,
                MarkedBy = markedBy,
                CreatedDate = DateTime.Now
            });
        }
        else
        {
            existing.Status = status;
            existing.Comment = comment;
            if (scannedAt.HasValue) existing.ScannedAt = scannedAt;
            if (minutesLate.HasValue) existing.MinutesLate = minutesLate;
            existing.Source = source;
            existing.MarkedBy = markedBy;
            existing.ModifiedDate = DateTime.Now;
        }

        if (sheet.Status == ClassAttendanceSheetStatus.NotStarted)
        {
            sheet.Status = ClassAttendanceSheetStatus.InProgress;
            sheet.ModifiedDate = DateTime.Now;
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task SubmitSheetAsync(
        int sheetId, string submittedBy, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);

        var sheet = await db.TcClassAttendanceSheets
            .FirstOrDefaultAsync(s => s.SheetId == sheetId, ct);
        if (sheet == null)
            throw new InvalidOperationException($"SheetId {sheetId} not found.");

        if (sheet.Status != ClassAttendanceSheetStatus.InProgress
            && sheet.Status != ClassAttendanceSheetStatus.Rejected
            && sheet.Status != ClassAttendanceSheetStatus.Reopened)
        {
            throw new InvalidOperationException(
                $"Sheet {sheetId} cannot transition from {sheet.Status} to Submitted.");
        }

        var cells = await db.TcClassAttendances
            .AsNoTracking()
            .Where(a => a.ClassSectionId == sheet.ClassSectionId
                     && a.AttendanceDate == sheet.SheetDate)
            .ToListAsync(ct);

        var enrolledCount = await db.TcSectionEnrollments
            .Where(e => e.ClassSectionId == sheet.ClassSectionId
                     && e.IsActive
                     && (e.DateEnrolled == null || e.DateEnrolled <= sheet.SheetDate)
                     && (e.DateLeft == null || e.DateLeft >= sheet.SheetDate))
            .CountAsync(ct);

        sheet.PresentCount  = cells.Count(c => c.Status == ClassAttendanceStatus.Present);
        sheet.TardyCount    = cells.Count(c => c.Status == ClassAttendanceStatus.Tardy);
        sheet.AbsentCount   = cells.Count(c => c.Status == ClassAttendanceStatus.Absent);
        sheet.ExcusedCount  = cells.Count(c => c.Status == ClassAttendanceStatus.Excused);
        sheet.EarlyOutCount = cells.Count(c => c.Status == ClassAttendanceStatus.EarlyOut);
        sheet.EnrolledCount = enrolledCount;

        if (sheet.Status == ClassAttendanceSheetStatus.Rejected)
            sheet.TeacherResubmitCount++;

        sheet.Status = ClassAttendanceSheetStatus.Submitted;
        sheet.SubmittedAt = DateTime.Now;
        sheet.SubmittedBy = submittedBy;
        sheet.ModifiedDate = DateTime.Now;

        await db.SaveChangesAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────

    private async Task<TcClassAttendanceSheet> EnsureSheetInternalAsync(
        TimeClockDbContext db, int classSectionId, DateOnly date, CancellationToken ct)
    {
        var sheet = await db.TcClassAttendanceSheets
            .FirstOrDefaultAsync(s => s.ClassSectionId == classSectionId
                                   && s.SheetDate == date, ct);
        if (sheet != null) return sheet;

        var section = await db.TcClassSections
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ClassSectionId == classSectionId, ct);
        if (section == null)
            throw new InvalidOperationException($"ClassSectionId {classSectionId} not found.");

        sheet = new TcClassAttendanceSheet
        {
            ClassSectionId = classSectionId,
            SheetDate = date,
            DistrictId = section.DistrictId,
            CampusId = section.CampusId,
            TeacherDcid = section.TeacherDcid,
            Status = ClassAttendanceSheetStatus.NotStarted,
            CreatedDate = DateTime.Now
        };
        db.TcClassAttendanceSheets.Add(sheet);
        await db.SaveChangesAsync(ct);
        return sheet;
    }
}
