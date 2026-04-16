using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Sub-facing slice of the substitute timecard service. Phase 2 covers the
/// sub's own timecard editing only — supervisor / HR methods arrive in
/// Phase 3 and Phase 4 respectively (see SubstituteTimesheetSpec.md section 20).
///
/// Key invariants (SubstituteTimesheetSpec.md):
///   * One TcSubstituteTimecard per (EmployeeId, CampusId, WorkDate).
///   * One TcSubstitutePeriodEntry per (SubTimecardId, PeriodNumber) — a sub
///     cannot bill the same period twice on the same card.
///   * Sub cannot add / remove / modify entries once the card is Approved or Locked.
///   * Audit writes happen AFTER SaveChanges succeeds (see Phase 1 handoff 3a).
///   * All CreatedDate / ModifiedDate stamps use DateTime.Now (local time,
///     Critical Rule 4.1 — never DateTime.UtcNow).
/// </summary>
public interface ISubstituteTimesheetService
{
    /// <summary>
    /// Returns the sub's timecard for today at <paramref name="campusId"/>,
    /// creating one if it does not yet exist. Newly-created cards are
    /// automatically linked to today's first In punch at that campus when
    /// available. Fires SUB_TIMECARD_CREATED on create only.
    /// </summary>
    Task<TcSubstituteTimecard> GetOrCreateTodayCardAsync(int employeeId, int campusId);

    /// <summary>
    /// All cards for the sub in the given pay-period window, eager-loading
    /// Campus + PeriodEntries (ordered by PeriodNumber) for UI rendering.
    /// Read-only — no audit.
    /// </summary>
    Task<List<TcSubstituteTimecard>> GetCardsForPayPeriodAsync(
        int employeeId, DateOnly payPeriodStart, DateOnly payPeriodEnd);

    /// <summary>
    /// Helper for the page: which campus did this sub check in at today?
    /// Returns the CampusId of the most recent In punch dated today, or null
    /// if the sub has not punched in yet. The page uses this to pre-select
    /// the campus before creating today's card.
    /// </summary>
    Task<int?> GetCampusIdFromTodayCheckInAsync(int employeeId);

    /// <summary>
    /// Adds one period entry to the card, snapshotting teacher / course /
    /// room / content-area from the master-schedule slot when provided.
    /// Throws on duplicate (PeriodNumber, SessionType) for the same card or
    /// when the card is not editable (Approved / Locked). Fires SUB_PERIOD_ADDED.
    /// </summary>
    Task<TcSubstitutePeriodEntry> AddPeriodEntryAsync(
        long subTimecardId,
        int bellPeriodId,
        int? masterScheduleId,
        string sessionType,
        string? notes,
        int employeeId);

    /// <summary>
    /// Removes one period entry. Blocked on Approved / Locked cards. Captures
    /// a pre-delete snapshot into the audit row's oldValues payload. Fires
    /// SUB_PERIOD_REMOVED.
    /// </summary>
    Task RemovePeriodEntryAsync(long entryId, int employeeId);

    /// <summary>
    /// Edits the Notes field of an existing entry. No-op if notes are
    /// unchanged. Blocked on Approved / Locked cards. Fires SUB_PERIOD_MODIFIED.
    /// </summary>
    Task UpdatePeriodNotesAsync(long entryId, string? newNotes, int employeeId);

    /// <summary>
    /// Phase 5 hook — will populate entries from accepted TcSubRequest records.
    /// No-op until the outreach / acceptance flow lands with migration 035
    /// (SubstituteTimesheetSpec.md section 15). Left on the interface so the
    /// page can call it unconditionally once Phase 5 lights it up.
    /// </summary>
    Task AutoPopulatePreAssignedAsync(long subTimecardId);
}

public class SubstituteTimesheetService : ISubstituteTimesheetService
{
    private readonly IDbContextFactory<TimeClockDbContext> _contextFactory;
    private readonly IAuditService _audit;
    private readonly IMasterScheduleLookupService _scheduleLookup;
    private readonly ILogger<SubstituteTimesheetService> _logger;

    public SubstituteTimesheetService(
        IDbContextFactory<TimeClockDbContext> contextFactory,
        IAuditService audit,
        IMasterScheduleLookupService scheduleLookup,
        ILogger<SubstituteTimesheetService> logger)
    {
        _contextFactory = contextFactory;
        _audit = audit;
        _scheduleLookup = scheduleLookup;
        _logger = logger;
    }

    public async Task<TcSubstituteTimecard> GetOrCreateTodayCardAsync(int employeeId, int campusId)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        using var context = await _contextFactory.CreateDbContextAsync();

        var existing = await context.TcSubstituteTimecards
            .Include(t => t.PeriodEntries)
            .FirstOrDefaultAsync(t => t.EmployeeId == employeeId
                                   && t.CampusId == campusId
                                   && t.WorkDate == today);
        if (existing != null)
            return existing;

        var card = new TcSubstituteTimecard
        {
            EmployeeId = employeeId,
            CampusId = campusId,
            WorkDate = today,
            TotalPeriodsWorked = 0,
            ApprovalStatus = ApprovalStatus.Pending,
            CreatedDate = DateTime.Now,
            ModifiedDate = DateTime.Now
        };

        var todayStart = DateTime.Today;
        var tomorrowStart = todayStart.AddDays(1);
        var checkInPunch = await context.TcTimePunches
            .Where(p => p.EmployeeId == employeeId
                     && p.CampusId == campusId
                     && p.PunchType == PunchType.In
                     && p.PunchDateTime >= todayStart
                     && p.PunchDateTime < tomorrowStart)
            .OrderBy(p => p.PunchDateTime)
            .FirstOrDefaultAsync();
        if (checkInPunch != null)
            card.CheckInPunchId = checkInPunch.PunchId;

        context.TcSubstituteTimecards.Add(card);
        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubTimecard.Created,
            entityType: AuditEntityTypes.SubTimecard,
            entityId: card.SubTimecardId.ToString(),
            newValues: new
            {
                card.EmployeeId,
                card.CampusId,
                WorkDate = card.WorkDate.ToString("yyyy-MM-dd"),
                card.CheckInPunchId,
                ApprovalStatus = card.ApprovalStatus.ToString()
            },
            deltaSummary: $"Created sub timecard for {today:yyyy-MM-dd} at campus {campusId}"
                        + (checkInPunch != null ? $" (linked punch {checkInPunch.PunchId})" : " (no check-in punch linked)"),
            source: AuditSource.AdminUi,
            employeeId: employeeId,
            campusId: campusId);

        return await context.TcSubstituteTimecards
            .Include(t => t.PeriodEntries)
            .FirstAsync(t => t.SubTimecardId == card.SubTimecardId);
    }

    public async Task<List<TcSubstituteTimecard>> GetCardsForPayPeriodAsync(
        int employeeId, DateOnly payPeriodStart, DateOnly payPeriodEnd)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        return await context.TcSubstituteTimecards
            .AsNoTracking()
            .Include(t => t.Campus)
            .Include(t => t.PeriodEntries.OrderBy(p => p.PeriodNumber))
            .Where(t => t.EmployeeId == employeeId
                     && t.WorkDate >= payPeriodStart
                     && t.WorkDate <= payPeriodEnd)
            .OrderBy(t => t.WorkDate)
            .ThenBy(t => t.CampusId)
            .ToListAsync();
    }

    public async Task<int?> GetCampusIdFromTodayCheckInAsync(int employeeId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();
        var todayStart = DateTime.Today;
        var tomorrowStart = todayStart.AddDays(1);
        var checkIn = await context.TcTimePunches
            .AsNoTracking()
            .Where(p => p.EmployeeId == employeeId
                     && p.PunchType == PunchType.In
                     && p.PunchDateTime >= todayStart
                     && p.PunchDateTime < tomorrowStart)
            .OrderByDescending(p => p.PunchDateTime)
            .FirstOrDefaultAsync();
        return checkIn?.CampusId;
    }

    public async Task<TcSubstitutePeriodEntry> AddPeriodEntryAsync(
        long subTimecardId,
        int bellPeriodId,
        int? masterScheduleId,
        string sessionType,
        string? notes,
        int employeeId)
    {
        if (string.IsNullOrWhiteSpace(sessionType))
            throw new ArgumentException("sessionType is required (DAY or NIGHT).", nameof(sessionType));

        var normalizedSession = sessionType.Trim().ToUpperInvariant();
        if (normalizedSession != "DAY" && normalizedSession != "NIGHT")
            throw new ArgumentException($"sessionType must be 'DAY' or 'NIGHT' (got '{sessionType}').", nameof(sessionType));

        using var context = await _contextFactory.CreateDbContextAsync();

        var card = await context.TcSubstituteTimecards
            .Include(t => t.PeriodEntries)
            .FirstOrDefaultAsync(t => t.SubTimecardId == subTimecardId);
        if (card == null)
            throw new InvalidOperationException($"Sub timecard {subTimecardId} not found.");
        if (card.EmployeeId != employeeId)
            throw new InvalidOperationException("Cannot modify another employee's timecard.");
        if (card.ApprovalStatus == ApprovalStatus.Approved || card.ApprovalStatus == ApprovalStatus.Locked)
            throw new InvalidOperationException("This timecard is approved or locked and cannot be modified.");

        var bellPeriod = await context.TcBellPeriods
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.PeriodId == bellPeriodId);
        if (bellPeriod == null)
            throw new InvalidOperationException($"Bell period {bellPeriodId} not found.");

        if (card.PeriodEntries.Any(e =>
                e.PeriodNumber == bellPeriod.PeriodNumber
             && string.Equals(e.SessionType, normalizedSession, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Period {bellPeriod.PeriodNumber} ({normalizedSession}) is already on this timecard.");
        }

        string? teacherName = null;
        string? courseName = null;
        string? contentArea = null;
        string? room = null;
        if (masterScheduleId.HasValue)
        {
            var ms = await context.TcMasterSchedules
                .AsNoTracking()
                .Include(m => m.Teacher)
                .FirstOrDefaultAsync(m => m.ScheduleId == masterScheduleId.Value);
            if (ms != null)
            {
                teacherName = ms.Teacher?.FullName
                              ?? (!string.IsNullOrWhiteSpace(ms.RawTeacherCell) ? ms.RawTeacherCell : null);
                courseName = ms.ContentArea;
                contentArea = ms.ContentArea;
                room = ms.Room;
            }
            else
            {
                _logger.LogWarning(
                    "AddPeriodEntryAsync: masterScheduleId {Id} not found — entry saved without denormalized snapshot.",
                    masterScheduleId.Value);
            }
        }

        var entry = new TcSubstitutePeriodEntry
        {
            SubTimecardId = card.SubTimecardId,
            BellPeriodId = bellPeriodId,
            MasterScheduleId = masterScheduleId,
            PeriodNumber = bellPeriod.PeriodNumber,
            PeriodName = bellPeriod.PeriodName,
            StartTime = bellPeriod.StartTime,
            EndTime = bellPeriod.EndTime,
            TeacherReplaced = teacherName,
            CourseName = courseName,
            ContentArea = contentArea,
            Room = room,
            SessionType = normalizedSession,
            EntrySource = "MANUAL",
            IsVerified = false,
            Notes = notes,
            CreatedDate = DateTime.Now
        };

        context.TcSubstitutePeriodEntries.Add(entry);
        card.TotalPeriodsWorked = card.PeriodEntries.Count + 1;
        card.ModifiedDate = DateTime.Now;

        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubTimecard.PeriodAdded,
            entityType: AuditEntityTypes.SubPeriodEntry,
            entityId: entry.EntryId.ToString(),
            newValues: new
            {
                entry.SubTimecardId,
                entry.PeriodNumber,
                entry.PeriodName,
                entry.SessionType,
                entry.TeacherReplaced,
                entry.CourseName,
                entry.ContentArea,
                entry.Room,
                entry.MasterScheduleId,
                entry.EntrySource
            },
            deltaSummary: $"Added P{entry.PeriodNumber} ({entry.SessionType}) — "
                        + (entry.TeacherReplaced ?? "no teacher snapshot"),
            source: AuditSource.AdminUi,
            employeeId: card.EmployeeId,
            campusId: card.CampusId);

        return entry;
    }

    public async Task RemovePeriodEntryAsync(long entryId, int employeeId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var entry = await context.TcSubstitutePeriodEntries
            .Include(e => e.SubTimecard)
            .FirstOrDefaultAsync(e => e.EntryId == entryId);
        if (entry == null)
            return;
        if (entry.SubTimecard == null)
            throw new InvalidOperationException("Period entry is detached from its timecard.");
        if (entry.SubTimecard.EmployeeId != employeeId)
            throw new InvalidOperationException("Cannot modify another employee's timecard.");
        if (entry.SubTimecard.ApprovalStatus == ApprovalStatus.Approved
         || entry.SubTimecard.ApprovalStatus == ApprovalStatus.Locked)
        {
            throw new InvalidOperationException("This timecard is approved or locked and cannot be modified.");
        }

        var snapshot = new
        {
            entry.SubTimecardId,
            entry.PeriodNumber,
            entry.PeriodName,
            entry.SessionType,
            entry.TeacherReplaced,
            entry.CourseName,
            entry.ContentArea,
            entry.Room,
            entry.MasterScheduleId,
            entry.EntrySource,
            entry.Notes
        };

        context.TcSubstitutePeriodEntries.Remove(entry);
        entry.SubTimecard.TotalPeriodsWorked = Math.Max(0, entry.SubTimecard.TotalPeriodsWorked - 1);
        entry.SubTimecard.ModifiedDate = DateTime.Now;

        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubTimecard.PeriodRemoved,
            entityType: AuditEntityTypes.SubPeriodEntry,
            entityId: entryId.ToString(),
            oldValues: snapshot,
            deltaSummary: $"Removed P{snapshot.PeriodNumber} ({snapshot.SessionType}) — "
                        + (snapshot.TeacherReplaced ?? "no teacher snapshot"),
            source: AuditSource.AdminUi,
            employeeId: entry.SubTimecard.EmployeeId,
            campusId: entry.SubTimecard.CampusId);
    }

    public async Task UpdatePeriodNotesAsync(long entryId, string? newNotes, int employeeId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var entry = await context.TcSubstitutePeriodEntries
            .Include(e => e.SubTimecard)
            .FirstOrDefaultAsync(e => e.EntryId == entryId);
        if (entry == null)
            return;
        if (entry.SubTimecard == null)
            throw new InvalidOperationException("Period entry is detached from its timecard.");
        if (entry.SubTimecard.EmployeeId != employeeId)
            throw new InvalidOperationException("Cannot modify another employee's timecard.");
        if (entry.SubTimecard.ApprovalStatus == ApprovalStatus.Approved
         || entry.SubTimecard.ApprovalStatus == ApprovalStatus.Locked)
        {
            throw new InvalidOperationException("This timecard is approved or locked and cannot be modified.");
        }

        var oldNotes = entry.Notes;
        var normalizedNew = string.IsNullOrWhiteSpace(newNotes) ? null : newNotes.Trim();
        var normalizedOld = string.IsNullOrWhiteSpace(oldNotes) ? null : oldNotes!.Trim();
        if (string.Equals(normalizedOld ?? "", normalizedNew ?? "", StringComparison.Ordinal))
            return;

        entry.Notes = normalizedNew;
        entry.SubTimecard.ModifiedDate = DateTime.Now;

        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubTimecard.PeriodModified,
            entityType: AuditEntityTypes.SubPeriodEntry,
            entityId: entryId.ToString(),
            oldValues: new { Notes = normalizedOld },
            newValues: new { Notes = normalizedNew },
            deltaSummary: $"Notes edited on P{entry.PeriodNumber} ({entry.SessionType})",
            source: AuditSource.AdminUi,
            employeeId: entry.SubTimecard.EmployeeId,
            campusId: entry.SubTimecard.CampusId);
    }

    public Task AutoPopulatePreAssignedAsync(long subTimecardId)
    {
        _logger.LogDebug(
            "AutoPopulatePreAssignedAsync is a Phase 5 no-op. Card {SubTimecardId} skipped until the outreach-accept flow lands with migration 035.",
            subTimecardId);
        return Task.CompletedTask;
    }
}
