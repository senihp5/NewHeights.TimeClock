using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Constants;
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
    /// Populate a card with PRE_ASSIGNED TcSubstitutePeriodEntry rows from a
    /// matching SubConfirmed TcSubRequest (matched on EmployeeId + CampusId +
    /// WorkDate between request StartDate/EndDate). Idempotent: skips if the
    /// card already has PRE_ASSIGNED entries for those periods. Safe to call
    /// for walk-in subs with no matching request — returns quietly. Fires one
    /// SUB_PERIOD_ADDED audit per entry created with source=SYSTEM.
    /// </summary>
    Task AutoPopulatePreAssignedAsync(long subTimecardId);

    /// <summary>
    /// Day-of kiosk auto-flow (Phase 7). Called from TimePunchService when a
    /// Substitute employee clocks In. Creates today's TcSubstituteTimecard for
    /// the given campus if it doesn't exist (linking the check-in punch), then
    /// auto-populates PRE_ASSIGNED entries from any matching SubConfirmed
    /// TcSubRequest. Walk-in subs (no matching request) just get an empty card.
    /// </summary>
    Task OnKioskCheckInAsync(int employeeId, int campusId, long? checkInPunchId);

    // ── Supervisor-facing (Phase 3) ───────────────────────────────────────

    /// <summary>
    /// Every sub timecard with at least one period entry in the given pay-period
    /// window, scoped by campus. Pass <paramref name="campusId"/> = null to
    /// return all campuses (for admin users). Includes Employee+Staff (name),
    /// Campus, and PeriodEntries (ordered) for UI rendering. No audit — read-only.
    /// </summary>
    Task<List<TcSubstituteTimecard>> GetCampusCardsForPayPeriodAsync(
        int? campusId, DateOnly payPeriodStart, DateOnly payPeriodEnd);

    /// <summary>
    /// Approves one sub timecard. Sets ApprovalStatus=Approved, stamps ApprovedBy
    /// + ApprovedDate from the supervisor's email. Throws if the card is already
    /// Approved or Locked. Fires SUB_TIMECARD_APPROVED with old/new status.
    /// </summary>
    Task ApproveCardAsync(long subTimecardId, string approverEmail);

    /// <summary>
    /// Rejects one sub timecard with a required reason. Sets ApprovalStatus=Rejected,
    /// stamps ApprovedBy + ApprovedDate (used as decidedBy/decidedDate semantically).
    /// Stores the reason in TcAuditLog.Reason — no schema change needed. Throws if
    /// reason is null/empty or the card is already Approved or Locked. Fires
    /// SUB_TIMECARD_REJECTED with old/new status and the reason on the audit row.
    /// </summary>
    Task RejectCardAsync(long subTimecardId, string approverEmail, string reason);

    // ── HR-facing (Phase 4) ───────────────────────────────────────────────

    /// <summary>
    /// One aggregate summary row per substitute who has any card in the given
    /// pay-period window. Includes per-campus + per-session period counts,
    /// supervisor-approval readiness, and an HRApproved flag derived from
    /// card-level Locked status. Read-only — no audit.
    /// </summary>
    Task<List<SubstitutePayrollSummary>> GetPayrollSummariesAsync(
        DateOnly periodStart, DateOnly periodEnd);

    /// <summary>
    /// HR approves one sub's payroll for the given pay-period. Validates every
    /// card for (employee, period) is in Approved status (throws if any are
    /// Pending / Rejected / already Locked), then flips all cards to Locked
    /// in a single transaction. Fires SUB_PAYROLL_APPROVED once for the batch.
    /// </summary>
    Task ApproveForPayrollAsync(
        int employeeId, DateOnly periodStart, DateOnly periodEnd, string approverEmail);

    /// <summary>
    /// Builds a CSV byte array with one row per sub per pay-period per spec
    /// section 12: EmpNbr, LastName, FirstName, PayPeriodStart, PayPeriodEnd,
    /// DayPeriods, NightPeriods, TotalPeriods, StopSixPeriods, McCartPeriods.
    /// Only includes subs with AllCardsLocked = true (HR-approved). Fires
    /// SUB_PAYROLL_EXPORTED once with aggregate metrics. Does NOT invoke the
    /// browser download — the caller is responsible for pushing the bytes.
    /// </summary>
    Task<byte[]> ExportSubstitutePayrollCsvAsync(
        DateOnly periodStart, DateOnly periodEnd, string exportedByEmail);

    /// <summary>
    /// Reverse a prior HR approval for a (sub, pay-period). Flips every Locked
    /// card for that employee in the date range back to Approved so edits can
    /// resume (sub can still edit because Phase 2's guard lets Approved cards
    /// stay read-only to subs, but HR can now re-open for supervisor re-review).
    /// Requires a non-empty reason stored in TcAuditLog.Reason. Fires one
    /// SUB_TIMECARD_UNLOCKED per card. Throws if no Locked cards found.
    /// </summary>
    Task UnlockCardsAsync(
        int employeeId, DateOnly periodStart, DateOnly periodEnd,
        string unlockedByEmail, string reason);
}

/// <summary>
/// Spec section 6.2 — one row per sub per pay-period, used by /hr/payroll
/// 'Substitutes' tab. Fields StopSix* / McCart* are hard-coded in the service
/// using AppConstants.Campus.StopSixPowerSchoolId / McCartPowerSchoolId to
/// keep the wire contract stable even if new campuses are added later.
/// </summary>
public class SubstitutePayrollSummary
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string FirstName { get; set; } = "";
    public string LastName { get; set; } = "";
    public string? AscenderEmployeeId { get; set; }
    public string? Email { get; set; }

    public int StopSixPeriods { get; set; }
    public int McCartPeriods { get; set; }
    public int DayPeriods { get; set; }
    public int NightPeriods { get; set; }
    public int TotalPeriods { get; set; }

    public int CardCount { get; set; }
    public int PendingCount { get; set; }
    public int ApprovedCount { get; set; }
    public int RejectedCount { get; set; }
    public int LockedCount { get; set; }

    /// <summary>True iff every card for this sub in the period is Approved status.</summary>
    public bool AllSupervisorApproved { get; set; }

    /// <summary>True iff every card for this sub in the period is Locked status.</summary>
    public bool HRApproved { get; set; }

    /// <summary>Most recent card's ApprovedDate — informational only.</summary>
    public DateTime? LastDecidedDate { get; set; }

    /// <summary>Employee's cards in the period — for expand-to-detail UI.</summary>
    public List<TcSubstituteTimecard> Cards { get; set; } = new();
}

public class SubstituteTimesheetService : ISubstituteTimesheetService
{
    private readonly IDbContextFactory<TimeClockDbContext> _contextFactory;
    private readonly IAuditService _audit;
    private readonly IMasterScheduleLookupService _scheduleLookup;
    private readonly IEmailService _emailService;
    private readonly ISmsService _smsService;
    private readonly ILogger<SubstituteTimesheetService> _logger;

    public SubstituteTimesheetService(
        IDbContextFactory<TimeClockDbContext> contextFactory,
        IAuditService audit,
        IMasterScheduleLookupService scheduleLookup,
        IEmailService emailService,
        ISmsService smsService,
        ILogger<SubstituteTimesheetService> logger)
    {
        _contextFactory = contextFactory;
        _audit = audit;
        _scheduleLookup = scheduleLookup;
        _emailService = emailService;
        _smsService = smsService;
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

    public async Task AutoPopulatePreAssignedAsync(long subTimecardId)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var card = await context.TcSubstituteTimecards
            .Include(t => t.PeriodEntries)
            .FirstOrDefaultAsync(t => t.SubTimecardId == subTimecardId);
        if (card == null)
        {
            _logger.LogDebug(
                "AutoPopulatePreAssignedAsync: card {SubTimecardId} not found — skipped.",
                subTimecardId);
            return;
        }

        if (card.ApprovalStatus == ApprovalStatus.Approved
         || card.ApprovalStatus == ApprovalStatus.Locked)
        {
            _logger.LogDebug(
                "AutoPopulatePreAssignedAsync: card {SubTimecardId} is {Status} — skipped.",
                subTimecardId, card.ApprovalStatus);
            return;
        }

        // Find a SubConfirmed request matching this sub + this campus + this work date.
        var request = await context.TcSubRequests
            .AsNoTracking()
            .Include(r => r.RequestingEmployee).ThenInclude(e => e.Staff)
            .FirstOrDefaultAsync(r => r.AssignedSubEmployeeId == card.EmployeeId
                                   && r.CampusId == card.CampusId
                                   && r.Status == SubRequestStatus.SubConfirmed
                                   && r.StartDate <= card.WorkDate
                                   && r.EndDate >= card.WorkDate);
        if (request == null)
        {
            _logger.LogDebug(
                "AutoPopulatePreAssignedAsync: no confirmed SubRequest matches card {SubTimecardId} (employee {EmployeeId}, campus {CampusId}, date {Date}). Walk-in — nothing to pre-assign.",
                subTimecardId, card.EmployeeId, card.CampusId, card.WorkDate);
            return;
        }

        // Parse PeriodsNeeded like "P1,P2,P3,P4" or "P1, P3, P5".
        var wantedPeriodNumbers = ParsePeriodsNeeded(request.PeriodsNeeded);
        if (wantedPeriodNumbers.Count == 0)
        {
            _logger.LogWarning(
                "AutoPopulatePreAssignedAsync: request {SubRequestId} has no parseable PeriodsNeeded ({Raw}). Nothing populated.",
                request.SubRequestId, request.PeriodsNeeded ?? "(null)");
            return;
        }

        // Determine session from the request. "BOTH" populates from DAY schedule only by
        // default — if a campus has separate evening classes, adjust the request to DAY
        // or NIGHT explicitly. This avoids double-populating when the sub is only
        // covering one half of the day.
        var sessionType = string.IsNullOrWhiteSpace(request.SessionType)
            ? "DAY"
            : request.SessionType.Trim().ToUpperInvariant() switch
            {
                "NIGHT" => "NIGHT",
                "EVENING" => "NIGHT",
                _ => "DAY"
            };

        // Load the campus bell schedule (matching session) for period times + names.
        var schedule = await context.TcBellSchedules
            .AsNoTracking()
            .Include(s => s.Periods)
            .Where(s => s.CampusId == card.CampusId
                     && s.SessionType == sessionType
                     && s.IsActive)
            .OrderByDescending(s => s.IsDefault)
            .ThenByDescending(s => s.CreatedDate)
            .FirstOrDefaultAsync();
        if (schedule == null)
        {
            _logger.LogWarning(
                "AutoPopulatePreAssignedAsync: no active {Session} bell schedule for campus {CampusId}. Request {SubRequestId} not auto-populated.",
                sessionType, card.CampusId, request.SubRequestId);
            return;
        }

        var bellPeriodsByNumber = schedule.Periods
            .Where(p => p.IsActive && p.PeriodType == "CLASS")
            .ToDictionary(p => p.PeriodNumber, p => p);

        // Idempotency: skip periods already present on the card (any source, any session).
        var existingNumbers = card.PeriodEntries
            .Select(e => e.PeriodNumber)
            .ToHashSet();

        var createdEntries = new List<TcSubstitutePeriodEntry>();

        foreach (var periodNumber in wantedPeriodNumbers)
        {
            if (existingNumbers.Contains(periodNumber))
                continue;
            if (!bellPeriodsByNumber.TryGetValue(periodNumber, out var bellPeriod))
            {
                _logger.LogWarning(
                    "AutoPopulatePreAssignedAsync: period P{Period} not found in {Session} schedule for campus {CampusId} — skipping.",
                    periodNumber, sessionType, card.CampusId);
                continue;
            }

            // Master-schedule snapshot (teacher / course / room / content).
            int? masterScheduleId = null;
            string? teacherName = null;
            string? courseName = null;
            string? contentArea = null;
            string? room = null;
            try
            {
                var slots = await _scheduleLookup.GetTeachersForPeriodAsync(
                    card.CampusId, card.WorkDate, periodNumber, sessionType);
                // Prefer the teacher being replaced (the requester) if found in the slots.
                var matched = slots.FirstOrDefault(s =>
                    string.Equals(s.TeacherName, request.RequestingEmployee?.Staff?.FullName,
                                  StringComparison.OrdinalIgnoreCase))
                            ?? slots.FirstOrDefault();
                if (matched != null)
                {
                    masterScheduleId = matched.MasterScheduleId;
                    teacherName = matched.TeacherName;
                    courseName = matched.CourseName ?? matched.ContentArea;
                    contentArea = matched.ContentArea;
                    room = matched.Room;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "AutoPopulatePreAssignedAsync: master-schedule lookup failed for campus {CampusId} P{Period} {Session}. Entry created without snapshot.",
                    card.CampusId, periodNumber, sessionType);
            }

            var entry = new TcSubstitutePeriodEntry
            {
                SubTimecardId = card.SubTimecardId,
                BellPeriodId = bellPeriod.PeriodId,
                MasterScheduleId = masterScheduleId,
                SubRequestId = request.SubRequestId,
                PeriodNumber = bellPeriod.PeriodNumber,
                PeriodName = bellPeriod.PeriodName,
                StartTime = bellPeriod.StartTime,
                EndTime = bellPeriod.EndTime,
                TeacherReplaced = teacherName
                                 ?? request.RequestingEmployee?.Staff?.FullName,
                CourseName = courseName,
                ContentArea = contentArea ?? request.SubjectArea,
                Room = room,
                SessionType = sessionType,
                EntrySource = "PRE_ASSIGNED",
                IsVerified = false,
                Notes = null,
                CreatedDate = DateTime.Now
            };

            context.TcSubstitutePeriodEntries.Add(entry);
            createdEntries.Add(entry);
            existingNumbers.Add(periodNumber);
        }

        if (createdEntries.Count == 0)
        {
            _logger.LogDebug(
                "AutoPopulatePreAssignedAsync: card {SubTimecardId} already had all requested periods — no new entries.",
                subTimecardId);
            return;
        }

        card.TotalPeriodsWorked += createdEntries.Count;
        card.ModifiedDate = DateTime.Now;

        await context.SaveChangesAsync();

        // One SUB_PERIOD_ADDED audit per entry — same code path subs would generate
        // manually, so the audit catalog stays consistent. Source=SYSTEM distinguishes
        // auto vs manual.
        foreach (var entry in createdEntries)
        {
            await _audit.LogActionAsync(
                actionCode: AuditActions.SubTimecard.PeriodAdded,
                entityType: AuditEntityTypes.SubPeriodEntry,
                entityId: entry.EntryId.ToString(),
                newValues: new
                {
                    entry.SubTimecardId,
                    entry.SubRequestId,
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
                deltaSummary: $"Auto-populated P{entry.PeriodNumber} ({entry.SessionType}) from sub request #{request.SubRequestId}",
                source: AuditSource.System,
                employeeId: card.EmployeeId,
                campusId: card.CampusId);
        }
    }

    public async Task OnKioskCheckInAsync(int employeeId, int campusId, long? checkInPunchId)
    {
        // Step 1: ensure today's card exists. GetOrCreateTodayCardAsync handles the
        // SUB_TIMECARD_CREATED audit on create and links the check-in punch if one
        // exists in the DB already. For the new punch that just landed (passed in
        // explicitly), we update the link post-create to be safe.
        var card = await GetOrCreateTodayCardAsync(employeeId, campusId);

        if (checkInPunchId.HasValue && card.CheckInPunchId != checkInPunchId.Value)
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var tracked = await context.TcSubstituteTimecards
                .FirstOrDefaultAsync(t => t.SubTimecardId == card.SubTimecardId);
            if (tracked != null && tracked.CheckInPunchId != checkInPunchId.Value)
            {
                tracked.CheckInPunchId = checkInPunchId.Value;
                tracked.ModifiedDate = DateTime.Now;
                await context.SaveChangesAsync();
            }
        }

        // Step 2: auto-populate PRE_ASSIGNED entries from any matching SubConfirmed request.
        // Idempotent — safe even if the sub swipes in multiple times in a day.
        try
        {
            await AutoPopulatePreAssignedAsync(card.SubTimecardId);
        }
        catch (Exception ex)
        {
            // Day-of automation failure should never break the punch flow.
            _logger.LogError(ex,
                "OnKioskCheckInAsync: auto-populate failed for card {SubTimecardId} (employee {EmployeeId}, campus {CampusId}). Punch already saved.",
                card.SubTimecardId, employeeId, campusId);
        }
    }

    /// <summary>
    /// Parse a PeriodsNeeded string like "P1,P2,P4" or "P1, P3" into a list of
    /// integers. Tolerates whitespace and mixed case. Non-parseable tokens are
    /// skipped silently.
    /// </summary>
    private static List<int> ParsePeriodsNeeded(string? raw)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;
        foreach (var chunk in raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = chunk.Trim();
            if (trimmed.StartsWith("P", StringComparison.OrdinalIgnoreCase))
                trimmed = trimmed.Substring(1);
            if (int.TryParse(trimmed, out var n) && n > 0 && n <= 20)
                result.Add(n);
        }
        return result.Distinct().OrderBy(n => n).ToList();
    }

    // ── Supervisor-facing implementations (Phase 3) ───────────────────────

    public async Task<List<TcSubstituteTimecard>> GetCampusCardsForPayPeriodAsync(
        int? campusId, DateOnly payPeriodStart, DateOnly payPeriodEnd)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var query = context.TcSubstituteTimecards
            .AsNoTracking()
            .Include(t => t.Employee!).ThenInclude(e => e.Staff)
            .Include(t => t.Campus)
            .Include(t => t.PeriodEntries.OrderBy(p => p.PeriodNumber))
            .Where(t => t.WorkDate >= payPeriodStart && t.WorkDate <= payPeriodEnd);

        if (campusId.HasValue)
            query = query.Where(t => t.CampusId == campusId.Value);

        return await query
            .OrderBy(t => t.EmployeeId)
            .ThenBy(t => t.WorkDate)
            .ThenBy(t => t.CampusId)
            .ToListAsync();
    }

    public async Task ApproveCardAsync(long subTimecardId, string approverEmail)
    {
        if (string.IsNullOrWhiteSpace(approverEmail))
            throw new ArgumentException("approverEmail is required.", nameof(approverEmail));

        using var context = await _contextFactory.CreateDbContextAsync();

        var card = await context.TcSubstituteTimecards
            .FirstOrDefaultAsync(t => t.SubTimecardId == subTimecardId);
        if (card == null)
            throw new InvalidOperationException($"Sub timecard {subTimecardId} not found.");
        if (card.ApprovalStatus == ApprovalStatus.Approved)
            throw new InvalidOperationException("This timecard is already approved.");
        if (card.ApprovalStatus == ApprovalStatus.Locked)
            throw new InvalidOperationException("This timecard is locked and cannot be approved.");

        var oldStatus = card.ApprovalStatus;
        var oldApprovedBy = card.ApprovedBy;
        var oldApprovedDate = card.ApprovedDate;

        card.ApprovalStatus = ApprovalStatus.Approved;
        card.ApprovedBy = approverEmail;
        card.ApprovedDate = DateTime.Now;
        card.ModifiedDate = DateTime.Now;

        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubTimecard.Approved,
            entityType: AuditEntityTypes.SubTimecard,
            entityId: card.SubTimecardId.ToString(),
            oldValues: new
            {
                ApprovalStatus = oldStatus.ToString(),
                ApprovedBy = oldApprovedBy,
                ApprovedDate = oldApprovedDate
            },
            newValues: new
            {
                ApprovalStatus = card.ApprovalStatus.ToString(),
                ApprovedBy = card.ApprovedBy,
                ApprovedDate = card.ApprovedDate,
                card.TotalPeriodsWorked
            },
            deltaSummary: $"Approved sub timecard for {card.WorkDate:yyyy-MM-dd} "
                        + $"({card.TotalPeriodsWorked} period{(card.TotalPeriodsWorked == 1 ? "" : "s")}) "
                        + $"by {approverEmail}",
            source: AuditSource.AdminUi,
            employeeId: card.EmployeeId,
            campusId: card.CampusId);

        // Phase 7a: notify the sub (SMS + email). Fire-and-forget — failure to notify
        // must not roll back the approval.
        await TryNotifySubApprovalAsync(card, approverEmail, isApproval: true, reason: null);
    }

    public async Task RejectCardAsync(long subTimecardId, string approverEmail, string reason)
    {
        if (string.IsNullOrWhiteSpace(approverEmail))
            throw new ArgumentException("approverEmail is required.", nameof(approverEmail));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A rejection reason is required.", nameof(reason));

        var trimmedReason = reason.Trim();
        if (trimmedReason.Length > 500)
            trimmedReason = trimmedReason.Substring(0, 500);

        using var context = await _contextFactory.CreateDbContextAsync();

        var card = await context.TcSubstituteTimecards
            .FirstOrDefaultAsync(t => t.SubTimecardId == subTimecardId);
        if (card == null)
            throw new InvalidOperationException($"Sub timecard {subTimecardId} not found.");
        if (card.ApprovalStatus == ApprovalStatus.Approved)
            throw new InvalidOperationException("This timecard is already approved. Unlock it first to reject.");
        if (card.ApprovalStatus == ApprovalStatus.Locked)
            throw new InvalidOperationException("This timecard is locked and cannot be rejected.");

        var oldStatus = card.ApprovalStatus;
        var oldApprovedBy = card.ApprovedBy;
        var oldApprovedDate = card.ApprovedDate;

        card.ApprovalStatus = ApprovalStatus.Rejected;
        card.ApprovedBy = approverEmail;
        card.ApprovedDate = DateTime.Now;
        card.ModifiedDate = DateTime.Now;

        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubTimecard.Rejected,
            entityType: AuditEntityTypes.SubTimecard,
            entityId: card.SubTimecardId.ToString(),
            oldValues: new
            {
                ApprovalStatus = oldStatus.ToString(),
                ApprovedBy = oldApprovedBy,
                ApprovedDate = oldApprovedDate
            },
            newValues: new
            {
                ApprovalStatus = card.ApprovalStatus.ToString(),
                ApprovedBy = card.ApprovedBy,
                ApprovedDate = card.ApprovedDate,
                card.TotalPeriodsWorked
            },
            deltaSummary: $"Rejected sub timecard for {card.WorkDate:yyyy-MM-dd} "
                        + $"({card.TotalPeriodsWorked} period{(card.TotalPeriodsWorked == 1 ? "" : "s")}) "
                        + $"by {approverEmail}",
            reason: trimmedReason,
            source: AuditSource.AdminUi,
            employeeId: card.EmployeeId,
            campusId: card.CampusId);

        // Phase 7a: notify the sub of the rejection with the reason.
        await TryNotifySubApprovalAsync(card, approverEmail, isApproval: false, reason: trimmedReason);
    }

    // ── Sub notifications on approve/reject (Phase 7a) ────────────────────

    /// <summary>
    /// Send SMS + email to the sub whose card was just approved or rejected. Reuses
    /// the Phase 6 SMS+email parallel pattern — email always fires, SMS fires when
    /// ACS is enabled + the sub has a phone + SmsOptedOut is false. Never throws —
    /// notification failure must not roll back the approval/rejection.
    /// </summary>
    private async Task TryNotifySubApprovalAsync(
        TcSubstituteTimecard card, string decidedByEmail, bool isApproval, string? reason)
    {
        try
        {
            using var context = await _contextFactory.CreateDbContextAsync();
            var sub = await context.TcEmployees
                .AsNoTracking()
                .Include(e => e.Staff)
                .FirstOrDefaultAsync(e => e.EmployeeId == card.EmployeeId);
            if (sub == null)
            {
                _logger.LogWarning(
                    "TryNotifySubApprovalAsync: sub {EmployeeId} not found. No notification sent for card {CardId}.",
                    card.EmployeeId, card.SubTimecardId);
                return;
            }

            var campusName = await context.Campuses
                .AsNoTracking()
                .Where(c => c.CampusId == card.CampusId)
                .Select(c => c.CampusName)
                .FirstOrDefaultAsync()
                ?? $"Campus {card.CampusId}";

            var subName = sub.Staff?.FirstName ?? sub.Staff?.FullName ?? "Substitute";
            var workDate = card.WorkDate.ToString("ddd, MMM d, yyyy");
            var verb = isApproval ? "approved" : "rejected";

            // SMS
            if (_smsService.IsEnabled && !sub.SmsOptedOut && !string.IsNullOrWhiteSpace(sub.Phone))
            {
                var smsBody = isApproval
                    ? $"New Heights: Your sub timecard for {campusName} on {workDate} was approved by {decidedByEmail}. Reply STOP to opt out."
                    : $"New Heights: Your sub timecard for {campusName} on {workDate} was rejected by {decidedByEmail}. Reason: {Truncate(reason, 80)}. Reply STOP to opt out.";
                if (smsBody.Length > 320) smsBody = smsBody.Substring(0, 320);
                await _smsService.SendAsync(sub.Phone!, smsBody);
            }

            // Email — always attempt if sub has an email.
            if (!string.IsNullOrWhiteSpace(sub.Email))
            {
                var subject = isApproval
                    ? $"Sub timecard approved — {campusName} {workDate}"
                    : $"Sub timecard rejected — {campusName} {workDate}";

                var color = isApproval ? "#059669" : "#b91c1c";
                var headline = isApproval ? "Your timecard was approved" : "Your timecard was rejected";
                var reasonBlock = !isApproval && !string.IsNullOrWhiteSpace(reason)
                    ? $"<p><strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(reason)}</p>"
                    : "";
                var nextSteps = isApproval
                    ? "<p>Your timecard will be forwarded to HR for payroll processing.</p>"
                    : "<p>Please contact your campus manager to discuss next steps.</p>";

                var html = $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  <h2 style='color: {color};'>{headline}</h2>
  <p>Hi {System.Net.WebUtility.HtmlEncode(subName)},</p>
  <p>Your substitute timecard for <strong>{System.Net.WebUtility.HtmlEncode(campusName)}</strong> on <strong>{workDate}</strong> ({card.TotalPeriodsWorked} period{(card.TotalPeriodsWorked == 1 ? "" : "s")}) was {verb} by {System.Net.WebUtility.HtmlEncode(decidedByEmail)}.</p>
  {reasonBlock}
  {nextSteps}
  <p style='color:#6b7280;font-size:0.85rem;'>Questions? Contact your campus manager.</p>
</div>";

                await _emailService.SendEmailAsync(sub.Email!, subject, html);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TryNotifySubApprovalAsync: notification send failed for card {CardId} (employee {EmployeeId}). Approval already recorded.",
                card.SubTimecardId, card.EmployeeId);
        }
    }

    private static string Truncate(string? value, int maxLen)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= maxLen ? value : value.Substring(0, maxLen - 1) + "…";
    }

    // ── HR-facing implementations (Phase 4) ───────────────────────────────

    public async Task<List<SubstitutePayrollSummary>> GetPayrollSummariesAsync(
        DateOnly periodStart, DateOnly periodEnd)
    {
        using var context = await _contextFactory.CreateDbContextAsync();

        var cards = await context.TcSubstituteTimecards
            .AsNoTracking()
            .Include(t => t.Employee!).ThenInclude(e => e.Staff)
            .Include(t => t.Campus)
            .Include(t => t.PeriodEntries)
            .Where(t => t.WorkDate >= periodStart && t.WorkDate <= periodEnd)
            .ToListAsync();

        var summaries = cards
            .GroupBy(c => c.EmployeeId)
            .Select(g =>
            {
                var cardList = g.OrderBy(c => c.WorkDate).ThenBy(c => c.CampusId).ToList();
                var emp = cardList[0].Employee;
                var staff = emp?.Staff;
                var entries = cardList.SelectMany(c => c.PeriodEntries).ToList();

                var stopSixPeriods = cardList
                    .Where(c => c.CampusId == AppConstants.Campus.StopSixPowerSchoolId)
                    .Sum(c => c.TotalPeriodsWorked);
                var mcCartPeriods = cardList
                    .Where(c => c.CampusId == AppConstants.Campus.McCartPowerSchoolId)
                    .Sum(c => c.TotalPeriodsWorked);
                var dayPeriods = entries
                    .Count(e => string.Equals(e.SessionType, "DAY", StringComparison.OrdinalIgnoreCase));
                var nightPeriods = entries
                    .Count(e => string.Equals(e.SessionType, "NIGHT", StringComparison.OrdinalIgnoreCase));

                var fullName = staff?.FullName
                             ?? emp?.Email
                             ?? $"Employee {g.Key}";

                return new SubstitutePayrollSummary
                {
                    EmployeeId = g.Key,
                    EmployeeName = fullName,
                    FirstName = staff?.FirstName ?? "",
                    LastName = staff?.LastName ?? "",
                    AscenderEmployeeId = emp?.AscenderEmployeeId,
                    Email = emp?.Email,
                    StopSixPeriods = stopSixPeriods,
                    McCartPeriods = mcCartPeriods,
                    DayPeriods = dayPeriods,
                    NightPeriods = nightPeriods,
                    TotalPeriods = cardList.Sum(c => c.TotalPeriodsWorked),
                    CardCount = cardList.Count,
                    PendingCount = cardList.Count(c => c.ApprovalStatus == ApprovalStatus.Pending),
                    ApprovedCount = cardList.Count(c => c.ApprovalStatus == ApprovalStatus.Approved),
                    RejectedCount = cardList.Count(c => c.ApprovalStatus == ApprovalStatus.Rejected),
                    LockedCount = cardList.Count(c => c.ApprovalStatus == ApprovalStatus.Locked),
                    AllSupervisorApproved = cardList.Count > 0
                        && cardList.All(c => c.ApprovalStatus == ApprovalStatus.Approved),
                    HRApproved = cardList.Count > 0
                        && cardList.All(c => c.ApprovalStatus == ApprovalStatus.Locked),
                    LastDecidedDate = cardList
                        .Where(c => c.ApprovedDate.HasValue)
                        .OrderByDescending(c => c.ApprovedDate)
                        .FirstOrDefault()?.ApprovedDate,
                    Cards = cardList
                };
            })
            .OrderBy(s => s.LastName)
            .ThenBy(s => s.FirstName)
            .ToList();

        return summaries;
    }

    public async Task ApproveForPayrollAsync(
        int employeeId, DateOnly periodStart, DateOnly periodEnd, string approverEmail)
    {
        if (string.IsNullOrWhiteSpace(approverEmail))
            throw new ArgumentException("approverEmail is required.", nameof(approverEmail));
        if (periodEnd < periodStart)
            throw new ArgumentException("periodEnd must be on or after periodStart.", nameof(periodEnd));

        using var context = await _contextFactory.CreateDbContextAsync();

        var cards = await context.TcSubstituteTimecards
            .Where(t => t.EmployeeId == employeeId
                     && t.WorkDate >= periodStart
                     && t.WorkDate <= periodEnd)
            .ToListAsync();

        if (cards.Count == 0)
            throw new InvalidOperationException(
                $"No sub timecards found for employee {employeeId} in {periodStart:yyyy-MM-dd} – {periodEnd:yyyy-MM-dd}.");

        var nonApprovable = cards
            .Where(c => c.ApprovalStatus != ApprovalStatus.Approved)
            .ToList();
        if (nonApprovable.Count > 0)
        {
            var pending = nonApprovable.Count(c => c.ApprovalStatus == ApprovalStatus.Pending);
            var rejected = nonApprovable.Count(c => c.ApprovalStatus == ApprovalStatus.Rejected);
            var locked = nonApprovable.Count(c => c.ApprovalStatus == ApprovalStatus.Locked);
            var bits = new List<string>();
            if (pending > 0) bits.Add($"{pending} pending");
            if (rejected > 0) bits.Add($"{rejected} rejected");
            if (locked > 0) bits.Add($"{locked} already locked");
            throw new InvalidOperationException(
                $"Cannot HR-approve payroll: {string.Join(", ", bits)} card(s) must be resolved first.");
        }

        var totalPeriods = cards.Sum(c => c.TotalPeriodsWorked);
        var campusIds = cards.Select(c => c.CampusId).Distinct().OrderBy(id => id).ToList();
        var cardIds = cards.Select(c => c.SubTimecardId).OrderBy(id => id).ToList();

        foreach (var c in cards)
        {
            c.ApprovalStatus = ApprovalStatus.Locked;
            c.ModifiedDate = DateTime.Now;
        }

        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubTimecard.PayrollApproved,
            entityType: AuditEntityTypes.SubTimecard,
            entityId: $"{employeeId}:payroll:{periodStart:yyyyMMdd}-{periodEnd:yyyyMMdd}",
            newValues: new
            {
                EmployeeId = employeeId,
                PeriodStart = periodStart.ToString("yyyy-MM-dd"),
                PeriodEnd = periodEnd.ToString("yyyy-MM-dd"),
                CardCount = cards.Count,
                TotalPeriods = totalPeriods,
                CampusIds = campusIds,
                CardIds = cardIds,
                HRApprovedBy = approverEmail,
                LockedAt = DateTime.Now
            },
            deltaSummary: $"HR locked sub payroll for employee {employeeId} "
                        + $"({cards.Count} card{(cards.Count == 1 ? "" : "s")}, "
                        + $"{totalPeriods} period{(totalPeriods == 1 ? "" : "s")}) "
                        + $"by {approverEmail}",
            source: AuditSource.AdminUi,
            employeeId: employeeId);
    }

    public async Task<byte[]> ExportSubstitutePayrollCsvAsync(
        DateOnly periodStart, DateOnly periodEnd, string exportedByEmail)
    {
        if (string.IsNullOrWhiteSpace(exportedByEmail))
            throw new ArgumentException("exportedByEmail is required.", nameof(exportedByEmail));

        var summaries = await GetPayrollSummariesAsync(periodStart, periodEnd);
        var included = summaries.Where(s => s.HRApproved).ToList();

        var csv = new StringBuilder();
        csv.Append("EmpNbr,LastName,FirstName,PayPeriodStart,PayPeriodEnd,")
           .AppendLine("DayPeriods,NightPeriods,TotalPeriods,StopSixPeriods,McCartPeriods");

        foreach (var s in included)
        {
            csv.Append(CsvEscape(s.AscenderEmployeeId ?? "")).Append(',')
               .Append(CsvEscape(s.LastName)).Append(',')
               .Append(CsvEscape(s.FirstName)).Append(',')
               .Append(periodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
               .Append(periodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
               .Append(s.DayPeriods).Append(',')
               .Append(s.NightPeriods).Append(',')
               .Append(s.TotalPeriods).Append(',')
               .Append(s.StopSixPeriods).Append(',')
               .Append(s.McCartPeriods)
               .AppendLine();
        }

        var bytes = Encoding.UTF8.GetBytes(csv.ToString());

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubTimecard.PayrollExported,
            entityType: AuditEntityTypes.PayPeriod,
            entityId: $"SUB_PAYROLL_EXPORT:{periodStart:yyyyMMdd}-{periodEnd:yyyyMMdd}:{DateTime.Now:yyyyMMddHHmmss}",
            newValues: new
            {
                PeriodStart = periodStart.ToString("yyyy-MM-dd"),
                PeriodEnd = periodEnd.ToString("yyyy-MM-dd"),
                SubCount = included.Count,
                TotalPeriods = included.Sum(s => s.TotalPeriods),
                DayPeriods = included.Sum(s => s.DayPeriods),
                NightPeriods = included.Sum(s => s.NightPeriods),
                StopSixPeriods = included.Sum(s => s.StopSixPeriods),
                McCartPeriods = included.Sum(s => s.McCartPeriods),
                FileSizeBytes = bytes.Length,
                ExportedBy = exportedByEmail,
                SkippedSubs = summaries.Count - included.Count
            },
            deltaSummary: $"Exported sub payroll CSV for {periodStart:MMM d}–{periodEnd:MMM d yyyy} "
                        + $"({included.Count} sub{(included.Count == 1 ? "" : "s")}, "
                        + $"{included.Sum(s => s.TotalPeriods)} period{(included.Sum(s => s.TotalPeriods) == 1 ? "" : "s")}) "
                        + $"by {exportedByEmail}",
            source: AuditSource.AdminUi);

        return bytes;
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var needsQuote = value.Contains(',')
                      || value.Contains('"')
                      || value.Contains('\n')
                      || value.Contains('\r');
        if (!needsQuote) return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public async Task UnlockCardsAsync(
        int employeeId, DateOnly periodStart, DateOnly periodEnd,
        string unlockedByEmail, string reason)
    {
        if (string.IsNullOrWhiteSpace(unlockedByEmail))
            throw new ArgumentException("unlockedByEmail is required.", nameof(unlockedByEmail));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("A reason is required to unlock payroll.", nameof(reason));
        if (periodEnd < periodStart)
            throw new ArgumentException("periodEnd must be on or after periodStart.", nameof(periodEnd));

        var trimmedReason = reason.Trim();
        if (trimmedReason.Length > 500)
            trimmedReason = trimmedReason.Substring(0, 500);

        using var context = await _contextFactory.CreateDbContextAsync();

        var locked = await context.TcSubstituteTimecards
            .Where(t => t.EmployeeId == employeeId
                     && t.WorkDate >= periodStart
                     && t.WorkDate <= periodEnd
                     && t.ApprovalStatus == ApprovalStatus.Locked)
            .ToListAsync();

        if (locked.Count == 0)
            throw new InvalidOperationException(
                $"No locked cards to unlock for employee {employeeId} in {periodStart:yyyy-MM-dd} – {periodEnd:yyyy-MM-dd}.");

        foreach (var card in locked)
        {
            card.ApprovalStatus = ApprovalStatus.Approved;
            card.ModifiedDate = DateTime.Now;
        }

        await context.SaveChangesAsync();

        // One SUB_TIMECARD_UNLOCKED audit per card, with the reason on each row so a
        // future read doesn't have to cross-reference a batch header.
        foreach (var card in locked)
        {
            await _audit.LogActionAsync(
                actionCode: AuditActions.SubTimecard.Unlocked,
                entityType: AuditEntityTypes.SubTimecard,
                entityId: card.SubTimecardId.ToString(),
                oldValues: new { ApprovalStatus = ApprovalStatus.Locked.ToString() },
                newValues: new
                {
                    ApprovalStatus = card.ApprovalStatus.ToString(),
                    UnlockedBy = unlockedByEmail,
                    UnlockedAt = card.ModifiedDate
                },
                deltaSummary: $"HR unlocked sub card for {card.WorkDate:yyyy-MM-dd} (was Locked) by {unlockedByEmail}",
                reason: trimmedReason,
                source: AuditSource.AdminUi,
                employeeId: card.EmployeeId,
                campusId: card.CampusId);
        }
    }
}
