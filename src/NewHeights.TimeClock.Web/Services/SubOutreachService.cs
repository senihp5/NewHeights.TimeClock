using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Manual = exactly one sub contacted, no queue.
/// AutoCascade = ordered queue; first sub sent immediately, decline/expiry advances to next.
/// </summary>
public enum OutreachMode
{
    Manual,
    AutoCascade
}

public interface ISubOutreachService
{
    /// <summary>
    /// Creates one TcSubOutreach row per sub in the given order. SequenceOrder=1
    /// (the first in the list) gets MessageSentAt populated immediately and the
    /// email fires. If mode=AutoCascade, the rest of the rows are queued with
    /// MessageSentAt=null. If mode=Manual, <paramref name="subEmployeeIds"/>
    /// must contain exactly one id. Transitions the parent TcSubRequest from
    /// AbsenceApproved → SubAssigned on first send. Audits SUB_SMS_SENT per
    /// email dispatched (the action code is a hold-over from the SMS-only spec).
    /// </summary>
    Task<List<TcSubOutreach>> SendOutreachAsync(
        long subRequestId, IList<int> subEmployeeIds, OutreachMode mode, string sentByEmail);

    /// <summary>
    /// Validates the token, marks the outreach ACCEPTED, writes a
    /// TcSubRequestAssignment row for the accepted periods, and transitions
    /// the parent request. Phase A (migration 048) allows partial acceptance:
    /// if <paramref name="selectedPeriods"/> is non-null, only those periods
    /// are claimed; remaining uncovered periods cascade to the next sub. If
    /// null, the sub claims all currently-remaining periods (the legacy
    /// "accept everything" behavior).
    ///
    /// Status transitions:
    ///   all periods covered → SubConfirmed (+ SUB_FULLY_COVERED audit,
    ///                         AssignedSubEmployeeId stamped on first-accept)
    ///   periods remaining   → PartiallyAssigned (+ SUB_PARTIAL_ACCEPTED audit,
    ///                         cascade fires for next queued sub)
    ///
    /// Throws if token is invalid / expired / already responded, or if
    /// selectedPeriods contains values not currently in the remaining set
    /// (e.g. another sub already claimed them).
    /// </summary>
    Task<TcSubOutreach> ProcessAcceptAsync(string token, IReadOnlyCollection<string>? selectedPeriods = null);

    /// <summary>
    /// Marks the outreach DECLINED. If there's a next queued row in the auto-
    /// cascade queue (same SubRequestId, higher SequenceOrder, AWAITING,
    /// MessageSentAt=null), advances to it and sends. Else reverts the parent
    /// request status from SubAssigned back to AbsenceApproved. Audits
    /// SUB_DECLINED + optionally SUB_REASSIGNED + SUB_SMS_SENT on cascade.
    /// </summary>
    Task<TcSubOutreach> ProcessDeclineAsync(string token);

    /// <summary>
    /// Background-job entry point. Finds AWAITING outreach rows whose token
    /// has expired, marks them EXPIRED, and auto-advances to the next queued
    /// row if applicable. Returns the count of rows expired. Callable from a
    /// scheduled job (Phase 7) or admin UI.
    /// </summary>
    Task<int> ExpireStaleTokensAsync();

    /// <summary>
    /// All TcSubOutreach rows for a request, ordered by SequenceOrder. Used by
    /// the supervisor page's outreach history panel.
    /// </summary>
    Task<List<TcSubOutreach>> GetOutreachForRequestAsync(long subRequestId);

    /// <summary>
    /// Returns the outreach matching this token, with SubRequest + SubEmployee
    /// navs loaded. Used by /sub/respond/{token} to render the accept/decline
    /// page. Returns null if token is unknown.
    /// </summary>
    Task<TcSubOutreach?> GetOutreachByTokenAsync(string token);

    /// <summary>
    /// Phase 7d gap close (post-9a): notify the requesting teacher via email + SMS
    /// that the admin has approved or denied their absence. If decision is DENIED
    /// and a sub had been confirmed, the assigned sub is also notified that the
    /// assignment has been canceled. Never throws — notification failures are
    /// logged but must not roll back the approve/deny DB transaction.
    /// </summary>
    /// <param name="subRequestId">The TcSubRequest.SubRequestId.</param>
    /// <param name="decision">"APPROVED" or "DENIED" (case-insensitive).</param>
    /// <param name="reason">Admin-provided reason (shown on DENIED; ignored on APPROVED).</param>
    /// <param name="adminEmail">Email of the admin who made the decision, for logs.</param>
    Task NotifyAbsenceDecisionAsync(
        long subRequestId, string decision, string? reason, string adminEmail);
}

public class SubOutreachService : ISubOutreachService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly IAuditService _audit;
    private readonly IEmailService _emailService;
    private readonly ISmsService _smsService;
    private readonly IConfiguration _configuration;
    private readonly SubOutreachOptions _options;
    private readonly ILogger<SubOutreachService> _logger;

    // Tokens: 48 random bytes base64-encoded = 64 chars. Crypto random, URL-safe.
    private const int TokenByteLength = 48;

    // Phase D2: token lifetime is now bound from config (SubOutreach:TokenValidityHours).
    // Min clamp = 1 hour so a misconfigured 0 doesn't expire tokens before the email
    // even reaches the sub. See SubOutreachOptions for default.
    private int TokenValidityHours => Math.Max(1, _options.TokenValidityHours);

    // Phase A ext: emergency requests use a compressed window so same-day
    // must-fills don't sit in a 2-hour queue. Min clamp = 5 minutes.
    private int EmergencyTokenValidityMinutes => Math.Max(5, _options.EmergencyTokenValidityMinutes);

    /// <summary>
    /// Compute token expiry relative to now based on whether the request is
    /// flagged as Emergency Fill. Used by SendOutreachAsync + the cascade
    /// advancer so every dispatch path respects the flag consistently.
    /// </summary>
    private DateTime ComputeTokenExpiry(TcSubRequest request)
    {
        return request.IsEmergency
            ? DateTime.Now.AddMinutes(EmergencyTokenValidityMinutes)
            : DateTime.Now.AddHours(TokenValidityHours);
    }

    // SMS body limits. ACS splits >160 chars into multi-segment messages (billed per
    // segment). We aim for <=320 chars so outreach fits in 2 segments at worst.
    private const int SmsMaxLength = 320;

    public SubOutreachService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IAuditService audit,
        IEmailService emailService,
        ISmsService smsService,
        IConfiguration configuration,
        Microsoft.Extensions.Options.IOptions<SubOutreachOptions> options,
        ILogger<SubOutreachService> logger)
    {
        _dbFactory = dbFactory;
        _audit = audit;
        _emailService = emailService;
        _smsService = smsService;
        _configuration = configuration;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Result of attempting a single outreach or confirmation dispatch. Each channel
    /// is independent — we always try both (if eligible) and record what actually sent.
    /// </summary>
    private record ChannelDispatchResult(
        bool SmsAttempted, bool SmsDelivered, string? SmsMessageId, string? SmsError,
        bool EmailAttempted, bool EmailDelivered, string? EmailError)
    {
        public bool AnyDelivered => SmsDelivered || EmailDelivered;
        public string OutreachMethodLabel =>
            (SmsDelivered, EmailDelivered) switch
            {
                (true, true) => "BOTH",
                (true, false) => "SMS",
                (false, true) => "EMAIL",
                _ => "NONE"
            };
    }

    // ── Send ─────────────────────────────────────────────────────────────

    public async Task<List<TcSubOutreach>> SendOutreachAsync(
        long subRequestId, IList<int> subEmployeeIds, OutreachMode mode, string sentByEmail)
    {
        if (subEmployeeIds == null || subEmployeeIds.Count == 0)
            throw new ArgumentException("At least one sub employee id is required.", nameof(subEmployeeIds));
        if (mode == OutreachMode.Manual && subEmployeeIds.Count != 1)
            throw new ArgumentException("Manual mode requires exactly one sub.", nameof(subEmployeeIds));
        if (string.IsNullOrWhiteSpace(sentByEmail))
            throw new ArgumentException("sentByEmail is required.", nameof(sentByEmail));

        using var context = await _dbFactory.CreateDbContextAsync();

        var request = await context.TcSubRequests
            .Include(r => r.RequestingEmployee).ThenInclude(e => e.Staff)
            .Include(r => r.Campus)
            .FirstOrDefaultAsync(r => r.SubRequestId == subRequestId);
        if (request == null)
            throw new InvalidOperationException($"Sub request {subRequestId} not found.");

        // Phase 9a (2026-04-20): teacher-driven flow allows AwaitingSub here too.
        // Lifecycle: AwaitingSub -> SubAssigned (outreach in flight) -> SubConfirmed (sub accepted)
        //         -> AbsenceApproved (admin final approval). Legacy "AbsenceApproved-as-intermediate"
        //         is still accepted so any in-flight pre-9a requests continue working.
        if (request.Status != SubRequestStatus.AwaitingSub
            && request.Status != SubRequestStatus.AbsenceApproved
            && request.Status != SubRequestStatus.SubAssigned)
        {
            throw new InvalidOperationException(
                $"Cannot send outreach — request is {request.Status}. Must be AwaitingSub, AbsenceApproved, or SubAssigned.");
        }

        // Load the candidate subs; keep caller's ordering to preserve the queue.
        var subs = await context.TcEmployees
            .Include(e => e.Staff)
            .Where(e => subEmployeeIds.Contains(e.EmployeeId) && e.IsActive)
            .ToListAsync();

        var orderedSubs = subEmployeeIds
            .Select(id => subs.FirstOrDefault(s => s.EmployeeId == id))
            .Where(e => e != null)
            .Select(e => e!)
            .ToList();

        if (orderedSubs.Count == 0)
            throw new InvalidOperationException("None of the selected employees are active.");

        // Find the next free sequence number to avoid colliding with prior outreach on this request.
        var maxSeq = await context.TcSubOutreach
            .Where(o => o.SubRequestId == subRequestId)
            .Select(o => (int?)o.SequenceOrder)
            .MaxAsync() ?? 0;

        var tokenExpiresAt = ComputeTokenExpiry(request);
        var outreachRows = new List<TcSubOutreach>();

        // Phase A ext: Emergency Fill requests broadcast to every candidate at
        // once (first-to-accept wins). Non-emergency keeps the existing cascade
        // pattern where only the first sub gets pinged and the rest are queued.
        bool broadcastAll = request.IsEmergency;

        for (int i = 0; i < orderedSubs.Count; i++)
        {
            var sub = orderedSubs[i];
            var row = new TcSubOutreach
            {
                SubRequestId = subRequestId,
                SubEmployeeId = sub.EmployeeId,
                OutreachMethod = "EMAIL",
                PhoneNumber = sub.Phone,
                EmailAddress = sub.Email,
                ResponseToken = GenerateToken(),
                TokenExpiresAt = tokenExpiresAt,
                DeliveryStatus = "PENDING",
                ResponseStatus = "AWAITING",
                SentBy = sentByEmail,
                SequenceOrder = maxSeq + i + 1,
                CreatedDate = DateTime.Now
            };

            // Emergency: every row sends immediately (broadcast). Normal: first
            // in list sends immediately; rest are queued for sequential cascade.
            bool sendNow = broadcastAll || i == 0;
            if (sendNow)
            {
                var dispatch = await TryDispatchOutreachAsync(row, request, sub);
                row.MessageSentAt = DateTime.Now;
                row.OutreachMethod = dispatch.OutreachMethodLabel;
                row.DeliveryStatus = dispatch.AnyDelivered ? "DELIVERED" : "FAILED";
                row.MessageId = dispatch.SmsMessageId;
                row.Notes = BuildDispatchNote(dispatch);
            }

            context.TcSubOutreach.Add(row);
            outreachRows.Add(row);
        }

        // First outreach for this request bumps status to SubAssigned.
        // From either AwaitingSub (Phase 9a teacher-driven) or AbsenceApproved (legacy supervisor-driven).
        if (request.Status == SubRequestStatus.AwaitingSub
            || request.Status == SubRequestStatus.AbsenceApproved)
        {
            request.Status = SubRequestStatus.SubAssigned;
            request.ModifiedDate = DateTime.Now;
        }

        await context.SaveChangesAsync();

        // Audit each *sent* outreach (not the queued ones). Phase 6 splits channels:
        // SUB_SMS_SENT / SUB_SMS_FAILED / SUB_EMAIL_SENT fire separately based on what
        // actually happened. OutreachMethod on the row indicates the net outcome (SMS /
        // EMAIL / BOTH / NONE).
        foreach (var row in outreachRows.Where(r => r.MessageSentAt.HasValue))
        {
            await AuditDispatchAsync(row, subRequestId, sentByEmail, AuditSource.AdminUi);
        }

        return outreachRows;
    }

    // ── Accept ───────────────────────────────────────────────────────────

    public async Task<TcSubOutreach> ProcessAcceptAsync(string token, IReadOnlyCollection<string>? selectedPeriods = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token is required.", nameof(token));

        using var context = await _dbFactory.CreateDbContextAsync();

        var outreach = await context.TcSubOutreach
            .Include(o => o.SubRequest).ThenInclude(r => r!.RequestingEmployee).ThenInclude(e => e!.Staff)
            .Include(o => o.SubRequest).ThenInclude(r => r!.Campus)
            .Include(o => o.SubRequest).ThenInclude(r => r!.Assignments)
            .Include(o => o.SubEmployee).ThenInclude(e => e!.Staff)
            .FirstOrDefaultAsync(o => o.ResponseToken == token);

        if (outreach == null)
            throw new InvalidOperationException("Invalid response token.");
        if (outreach.ResponseStatus != "AWAITING")
            throw new InvalidOperationException(
                $"This link has already been used (status: {outreach.ResponseStatus}).");
        if (outreach.TokenExpiresAt < DateTime.Now)
            throw new InvalidOperationException(
                "This link has expired. Please contact your campus manager.");

        var request = outreach.SubRequest!;

        // Compute the remaining-periods set as of this accept. If the request
        // has no PeriodsNeeded at all, treat the full acceptance as a single
        // opaque "whole request" covering the empty string — legacy behavior
        // where the request wasn't period-scoped.
        var needed   = ParsePeriodSet(request.PeriodsNeeded);
        var covered  = request.Assignments
            .SelectMany(a => ParsePeriodSet(a.PeriodsCovered))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var remaining = needed.Except(covered, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Determine which periods this sub is claiming.
        HashSet<string> accepted;
        if (selectedPeriods == null || selectedPeriods.Count == 0)
        {
            // Legacy "accept everything" path — take all remaining.
            accepted = new HashSet<string>(remaining, StringComparer.OrdinalIgnoreCase);
        }
        else
        {
            accepted = new HashSet<string>(
                selectedPeriods.Select(p => p.Trim().ToUpperInvariant()).Where(p => p.Length > 0),
                StringComparer.OrdinalIgnoreCase);

            // Guard: only allow periods that are still remaining. If the sub
            // picked one that got claimed between page load and submit,
            // reject so we never write a conflicting assignment row.
            var invalid = accepted.Except(remaining, StringComparer.OrdinalIgnoreCase).ToList();
            if (invalid.Count > 0)
            {
                throw new InvalidOperationException(
                    $"The following periods are no longer available: {string.Join(", ", invalid)}. Reload the page and try again.");
            }
        }

        // Guard: at least one period (or legacy request with no periods).
        if (needed.Count > 0 && accepted.Count == 0)
        {
            throw new InvalidOperationException("Select at least one period to accept.");
        }

        // Mark outreach accepted regardless — token is single-use (per design
        // decision 3: sub cannot come back to claim more later).
        outreach.ResponseStatus = "ACCEPTED";
        outreach.RespondedAt = DateTime.Now;

        // Write the assignment row. PeriodsCovered is canonicalized (sorted,
        // comma-joined, upper). For a legacy no-periods request the stored
        // value is "" and the row acts as "this sub covers the whole thing."
        var assignment = new TcSubRequestAssignment
        {
            SubRequestId   = request.SubRequestId,
            SubEmployeeId  = outreach.SubEmployeeId,
            PeriodsCovered = FormatPeriodSet(accepted),
            AcceptedAt     = outreach.RespondedAt!.Value,
            CreatedDate    = DateTime.Now
        };
        context.TcSubRequestAssignments.Add(assignment);

        // Recompute coverage after this accept.
        var newCovered   = covered.Union(accepted, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newRemaining = needed.Except(newCovered, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool fullyCovered = newRemaining.Count == 0;

        // Stamp AssignedSubEmployeeId on first-accept for backward compat with
        // display code that reads the singular column. Later partial accepts
        // leave it alone; the join table is the authoritative truth.
        bool isFirstAssignment = request.Assignments.Count == 0;
        if (isFirstAssignment)
        {
            request.AssignedSubEmployeeId = outreach.SubEmployeeId;
            request.AssignedDate = DateTime.Now;
        }

        if (fullyCovered)
        {
            request.Status = SubRequestStatus.SubConfirmed;
            // Clear stall-alert dedup — request is done.
            request.PartialStallAlertSentAt = null;
        }
        else
        {
            request.Status = SubRequestStatus.PartiallyAssigned;
            // Reset stall clock — the request just saw progress.
            request.PartialStallAlertSentAt = null;
        }
        request.ModifiedDate = DateTime.Now;

        await context.SaveChangesAsync();

        // Audit the existing SUB_ACCEPTED code + the new partial/full code.
        await _audit.LogActionAsync(
            actionCode: AuditActions.SubOutreach.Accepted,
            entityType: AuditEntityTypes.SubOutreach,
            entityId: outreach.OutreachId.ToString(),
            newValues: new
            {
                outreach.SubRequestId,
                outreach.SubEmployeeId,
                AcceptedAt = outreach.RespondedAt,
                PeriodsCovered = assignment.PeriodsCovered,
                RequestStatus = request.Status.ToString()
            },
            deltaSummary: $"Sub employee {outreach.SubEmployeeId} accepted sub request {outreach.SubRequestId} (periods: {(assignment.PeriodsCovered.Length > 0 ? assignment.PeriodsCovered : "all")})",
            source: AuditSource.PublicLink,
            employeeId: outreach.SubEmployeeId);

        await _audit.LogActionAsync(
            actionCode: fullyCovered
                ? AuditActions.SubOutreach.FullyCovered
                : AuditActions.SubOutreach.PartialAccepted,
            entityType: AuditEntityTypes.SubRequest,
            entityId: request.SubRequestId.ToString(),
            newValues: new
            {
                request.SubRequestId,
                Covered = string.Join(",", newCovered.OrderBy(p => p)),
                Remaining = string.Join(",", newRemaining.OrderBy(p => p)),
                AcceptingSubEmployeeId = outreach.SubEmployeeId
            },
            deltaSummary: fullyCovered
                ? $"Sub request {request.SubRequestId} fully covered after employee {outreach.SubEmployeeId} accepted {assignment.PeriodsCovered}"
                : $"Sub request {request.SubRequestId} partially covered ({FormatPeriodSet(newCovered)} of {FormatPeriodSet(needed)}); remaining: {FormatPeriodSet(newRemaining)}",
            source: AuditSource.PublicLink,
            employeeId: outreach.SubEmployeeId);

        // Confirmation email to the accepting sub — mentions only the periods
        // they committed to. Done before cascade so the sub gets their
        // confirmation before the next sub in line gets a pitch.
        var acceptedPeriodsDisplay = assignment.PeriodsCovered.Length > 0
            ? assignment.PeriodsCovered
            : (request.PeriodsNeeded ?? "—");
        var confirm = await TryDispatchConfirmationAsync(outreach, request, acceptedPeriodsDisplay);
        if (confirm.AnyDelivered)
        {
            request.ConfirmationSentAt = DateTime.Now;
            await context.SaveChangesAsync();

            await _audit.LogActionAsync(
                actionCode: AuditActions.SubOutreach.ConfirmationSent,
                entityType: AuditEntityTypes.SubOutreach,
                entityId: outreach.OutreachId.ToString(),
                newValues: new
                {
                    Channels = confirm.OutreachMethodLabel,
                    SmsMessageId = confirm.SmsMessageId
                },
                deltaSummary: $"Sent confirmation ({confirm.OutreachMethodLabel}) to employee {outreach.SubEmployeeId} for sub request {outreach.SubRequestId}",
                source: AuditSource.System,
                employeeId: outreach.SubEmployeeId);
        }

        // Phase 7d: notify supervisor + requesting employee.
        await TryNotifyStakeholdersAsync(outreach, request, "ACCEPTED");

        // Phase A: if the request is only partially covered, fire cascade to
        // the next queued sub so remaining periods can find coverage. The
        // dispatch path computes remaining at send time so the next sub's
        // accept page shows only uncovered periods.
        if (!fullyCovered)
        {
            await AdvanceQueueOrRevertAsync(context, request, request.SubRequestId, outreach.SequenceOrder);
        }

        return outreach;
    }

    // ── Period-set helpers (Phase A) ─────────────────────────────────────

    /// <summary>
    /// Parse a comma-separated period list into a case-insensitive set.
    /// Empty/whitespace entries are skipped. Trimmed and uppercased so
    /// "P1, p2 , ,P3" → {"P1", "P2", "P3"}.
    /// </summary>
    private static HashSet<string> ParsePeriodSet(string? csv)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(csv)) return result;
        foreach (var raw in csv.Split(','))
        {
            var p = raw.Trim().ToUpperInvariant();
            if (p.Length > 0) result.Add(p);
        }
        return result;
    }

    /// <summary>
    /// Canonical CSV form: sorted alphabetically, comma-joined, upper.
    /// Empty set returns "". Used to persist PeriodsCovered so audit
    /// diffs and display are stable across accepts.
    /// </summary>
    private static string FormatPeriodSet(IEnumerable<string> set)
    {
        return string.Join(",", set.Select(p => p.Trim().ToUpperInvariant()).Where(p => p.Length > 0).OrderBy(p => p));
    }

    // ── Decline ──────────────────────────────────────────────────────────

    public async Task<TcSubOutreach> ProcessDeclineAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token is required.", nameof(token));

        using var context = await _dbFactory.CreateDbContextAsync();

        var outreach = await context.TcSubOutreach
            .Include(o => o.SubRequest).ThenInclude(r => r!.RequestingEmployee).ThenInclude(e => e!.Staff)
            .Include(o => o.SubRequest).ThenInclude(r => r!.Campus)
            .FirstOrDefaultAsync(o => o.ResponseToken == token);

        if (outreach == null)
            throw new InvalidOperationException("Invalid response token.");
        if (outreach.ResponseStatus != "AWAITING")
            throw new InvalidOperationException(
                $"This link has already been used (status: {outreach.ResponseStatus}).");
        if (outreach.TokenExpiresAt < DateTime.Now)
            throw new InvalidOperationException(
                "This link has expired. Please contact your campus manager.");

        outreach.ResponseStatus = "DECLINED";
        outreach.RespondedAt = DateTime.Now;
        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubOutreach.Declined,
            entityType: AuditEntityTypes.SubOutreach,
            entityId: outreach.OutreachId.ToString(),
            oldValues: new { PreviousStatus = "AWAITING" },
            newValues: new
            {
                outreach.SubRequestId,
                outreach.SubEmployeeId,
                DeclinedAt = outreach.RespondedAt,
                outreach.SequenceOrder
            },
            deltaSummary: $"Sub employee {outreach.SubEmployeeId} declined sub request {outreach.SubRequestId} (seq {outreach.SequenceOrder})",
            source: AuditSource.PublicLink,
            employeeId: outreach.SubEmployeeId);

        // Phase 7d: notify supervisor + requesting employee of the decline.
        await TryNotifyStakeholdersAsync(outreach, outreach.SubRequest!, "DECLINED");

        await AdvanceQueueOrRevertAsync(context, outreach.SubRequest!, outreach.SubRequestId, outreach.SequenceOrder);

        return outreach;
    }

    // ── Expire stale tokens ──────────────────────────────────────────────

    public async Task<int> ExpireStaleTokensAsync()
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        var now = DateTime.Now;

        var stale = await context.TcSubOutreach
            .Include(o => o.SubRequest).ThenInclude(r => r!.RequestingEmployee).ThenInclude(e => e!.Staff)
            .Include(o => o.SubRequest).ThenInclude(r => r!.Campus)
            .Where(o => o.ResponseStatus == "AWAITING"
                     && o.MessageSentAt != null
                     && o.TokenExpiresAt < now)
            .ToListAsync();

        if (stale.Count == 0) return 0;

        foreach (var outreach in stale)
        {
            outreach.ResponseStatus = "EXPIRED";
            outreach.RespondedAt = now;
        }
        await context.SaveChangesAsync();

        foreach (var outreach in stale)
        {
            await _audit.LogActionAsync(
                actionCode: AuditActions.SubOutreach.TokenExpired,
                entityType: AuditEntityTypes.SubOutreach,
                entityId: outreach.OutreachId.ToString(),
                deltaSummary: $"Outreach token expired for employee {outreach.SubEmployeeId} "
                            + $"on sub request {outreach.SubRequestId}",
                source: AuditSource.System,
                employeeId: outreach.SubEmployeeId);

            await AdvanceQueueOrRevertAsync(context, outreach.SubRequest!, outreach.SubRequestId, outreach.SequenceOrder);
        }

        return stale.Count;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    public async Task<List<TcSubOutreach>> GetOutreachForRequestAsync(long subRequestId)
    {
        using var context = await _dbFactory.CreateDbContextAsync();
        return await context.TcSubOutreach
            .AsNoTracking()
            .Include(o => o.SubEmployee).ThenInclude(e => e!.Staff)
            .Where(o => o.SubRequestId == subRequestId)
            .OrderBy(o => o.SequenceOrder)
            .ToListAsync();
    }

    public async Task<TcSubOutreach?> GetOutreachByTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var context = await _dbFactory.CreateDbContextAsync();
        return await context.TcSubOutreach
            .AsNoTracking()
            .Include(o => o.SubRequest).ThenInclude(r => r!.RequestingEmployee).ThenInclude(e => e!.Staff)
            .Include(o => o.SubRequest).ThenInclude(r => r!.Campus)
            .Include(o => o.SubEmployee).ThenInclude(e => e!.Staff)
            .FirstOrDefaultAsync(o => o.ResponseToken == token);
    }

    /// <summary>
    /// After a decline or expiry, look for the next queued sub in the cascade.
    /// If found, send to them + mark MessageSentAt + audit SUB_REASSIGNED + SUB_SMS_SENT.
    /// If none, revert the request from SubAssigned back to AbsenceApproved so the
    /// supervisor sees it as still needing a sub.
    /// </summary>
    private async Task AdvanceQueueOrRevertAsync(
        TimeClockDbContext context, TcSubRequest request, long subRequestId, int currentSequence)
    {
        var nextQueued = await context.TcSubOutreach
            .Include(o => o.SubEmployee).ThenInclude(e => e!.Staff)
            .Where(o => o.SubRequestId == subRequestId
                     && o.SequenceOrder > currentSequence
                     && o.ResponseStatus == "AWAITING"
                     && o.MessageSentAt == null)
            .OrderBy(o => o.SequenceOrder)
            .FirstOrDefaultAsync();

        if (nextQueued == null)
        {
            // Phase 9a: revert to AwaitingSub so the teacher (or admin via override)
            // can dispatch another round of outreach. Was AbsenceApproved pre-9a;
            // changed because in the new flow AbsenceApproved is the terminal/final
            // admin-approved state and shouldn't be reused as an intermediate.
            if (request.Status == SubRequestStatus.SubAssigned)
            {
                request.Status = SubRequestStatus.AwaitingSub;
                request.ModifiedDate = DateTime.Now;
                await context.SaveChangesAsync();
            }
            return;
        }

        // Phase A: if the request is partially covered, compute remaining
        // periods so the dispatch email shows only what's still uncovered.
        // Fresh requests with no assignments use request.PeriodsNeeded as-is.
        string? periodsOverride = null;
        if (request.Status == SubRequestStatus.PartiallyAssigned)
        {
            var coveredRaw = await context.TcSubRequestAssignments
                .Where(a => a.SubRequestId == request.SubRequestId)
                .Select(a => a.PeriodsCovered)
                .ToListAsync();
            var needed = ParsePeriodSet(request.PeriodsNeeded);
            var coveredSet = coveredRaw
                .SelectMany(c => ParsePeriodSet(c))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var remaining = needed.Except(coveredSet, StringComparer.OrdinalIgnoreCase);
            periodsOverride = FormatPeriodSet(remaining);
        }

        // Refresh token expiry for this send, then dispatch via both channels.
        // Phase A ext: ComputeTokenExpiry honors request.IsEmergency so a
        // mid-cascade send on an emergency request uses the 30-minute window.
        nextQueued.TokenExpiresAt = ComputeTokenExpiry(request);
        var dispatch = await TryDispatchOutreachAsync(nextQueued, request, nextQueued.SubEmployee!, periodsOverride);
        nextQueued.MessageSentAt = DateTime.Now;
        nextQueued.OutreachMethod = dispatch.OutreachMethodLabel;
        nextQueued.DeliveryStatus = dispatch.AnyDelivered ? "DELIVERED" : "FAILED";
        nextQueued.MessageId = dispatch.SmsMessageId;
        nextQueued.Notes = BuildDispatchNote(dispatch);
        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubOutreach.Reassigned,
            entityType: AuditEntityTypes.SubOutreach,
            entityId: nextQueued.OutreachId.ToString(),
            newValues: new
            {
                nextQueued.SubRequestId,
                nextQueued.SubEmployeeId,
                nextQueued.SequenceOrder,
                Cause = $"previous-sub-outreach-resolved-seq-{currentSequence}"
            },
            deltaSummary: $"Auto-cascade: advanced to sub employee {nextQueued.SubEmployeeId} (seq {nextQueued.SequenceOrder}) for sub request {subRequestId}",
            source: AuditSource.System,
            employeeId: nextQueued.SubEmployeeId);

        await AuditDispatchAsync(nextQueued, subRequestId,
            sentByEmail: nextQueued.SentBy ?? "system",
            source: AuditSource.System);
    }

    // ── Audit helper (Phase 6 per-channel) ───────────────────────────────

    /// <summary>
    /// Emits the correct mix of SUB_SMS_SENT / SUB_SMS_FAILED / SUB_EMAIL_SENT
    /// for an outreach row, based on its OutreachMethod + Notes summary.
    /// </summary>
    private async Task AuditDispatchAsync(
        TcSubOutreach row, long subRequestId, string sentByEmail, string source)
    {
        var method = row.OutreachMethod ?? "NONE";
        var anyDelivered = method != "NONE";

        // SMS audits
        if (method == "BOTH" || method == "SMS")
        {
            await _audit.LogActionAsync(
                actionCode: AuditActions.SubOutreach.SmsSent,
                entityType: AuditEntityTypes.SubOutreach,
                entityId: row.OutreachId.ToString(),
                newValues: new
                {
                    row.SubRequestId,
                    row.SubEmployeeId,
                    row.SequenceOrder,
                    row.PhoneNumber,
                    row.MessageId,
                    SentBy = sentByEmail
                },
                deltaSummary: $"Sent SMS (seq {row.SequenceOrder}) to employee {row.SubEmployeeId} for sub request {subRequestId}",
                source: source,
                employeeId: row.SubEmployeeId);
        }
        else if (!string.IsNullOrWhiteSpace(row.PhoneNumber) && method != "BOTH" && method != "SMS")
        {
            // SMS was attempted (sub has a phone) but not delivered. Note: we only know
            // the "attempted" signal from BuildDispatchNote's SmsAttempted flag stored in Notes.
            // If Notes contains "sms-attempted", audit the failure explicitly.
            if (row.Notes != null && row.Notes.Contains("sms-attempted", StringComparison.OrdinalIgnoreCase))
            {
                await _audit.LogActionAsync(
                    actionCode: AuditActions.SubOutreach.SmsFailed,
                    entityType: AuditEntityTypes.SubOutreach,
                    entityId: row.OutreachId.ToString(),
                    newValues: new
                    {
                        row.SubRequestId,
                        row.SubEmployeeId,
                        row.SequenceOrder,
                        row.PhoneNumber,
                        DispatchNote = row.Notes,
                        SentBy = sentByEmail
                    },
                    deltaSummary: $"SMS failed (seq {row.SequenceOrder}) for employee {row.SubEmployeeId} on sub request {subRequestId}",
                    source: source,
                    employeeId: row.SubEmployeeId);
            }
        }

        // Email audits
        if (method == "BOTH" || method == "EMAIL")
        {
            await _audit.LogActionAsync(
                actionCode: AuditActions.SubOutreach.EmailSent,
                entityType: AuditEntityTypes.SubOutreach,
                entityId: row.OutreachId.ToString(),
                newValues: new
                {
                    row.SubRequestId,
                    row.SubEmployeeId,
                    row.SequenceOrder,
                    row.EmailAddress,
                    SentBy = sentByEmail
                },
                deltaSummary: $"Sent email (seq {row.SequenceOrder}) to employee {row.SubEmployeeId} for sub request {subRequestId}",
                source: source,
                employeeId: row.SubEmployeeId);
        }

        if (!anyDelivered)
        {
            _logger.LogWarning(
                "Dispatch landed NONE for outreach {OutreachId} — neither SMS nor email delivered. Notes: {Notes}",
                row.OutreachId, row.Notes);
        }
    }

    private static string BuildDispatchNote(ChannelDispatchResult dispatch)
    {
        var parts = new List<string>();
        if (dispatch.SmsAttempted)
            parts.Add(dispatch.SmsDelivered
                ? "sms-attempted:delivered"
                : $"sms-attempted:failed:{dispatch.SmsError ?? "unknown"}");
        if (dispatch.EmailAttempted)
            parts.Add(dispatch.EmailDelivered
                ? "email-attempted:delivered"
                : $"email-attempted:failed:{dispatch.EmailError ?? "unknown"}");
        return parts.Count == 0 ? "no-channel-attempted" : string.Join(" | ", parts);
    }

    // ── Dispatch (Phase 6: SMS + email parallel) ─────────────────────────

    /// <summary>
    /// Attempts SMS (if the service is enabled + sub has phone + not SmsOptedOut)
    /// AND email (if sub has email) for an outreach send. Each channel is
    /// independent — a failure on one does not abort the other. Returns a result
    /// describing what actually happened for audit + TcSubOutreach bookkeeping.
    /// The outreach row is required so the accept link can include its token.
    /// </summary>
    private async Task<ChannelDispatchResult> TryDispatchOutreachAsync(
        TcSubOutreach outreach, TcSubRequest request, TcEmployee sub,
        string? periodsOverride = null)
    {
        // SMS eligibility
        var smsEligible = _smsService.IsEnabled
                       && !sub.SmsOptedOut
                       && !string.IsNullOrWhiteSpace(sub.Phone);

        var smsAttempted = false;
        var smsDelivered = false;
        string? smsMessageId = null;
        string? smsError = null;

        if (smsEligible)
        {
            var smsBody = BuildSmsOutreachBody(outreach, request);
            var smsResult = await _smsService.SendAsync(sub.Phone!, smsBody);
            smsAttempted = smsResult.Attempted;
            smsDelivered = smsResult.Delivered;
            smsMessageId = smsResult.MessageId;
            smsError = smsResult.ErrorReason;
        }

        // Email
        var emailAttempted = false;
        var emailDelivered = false;
        string? emailError = null;
        if (!string.IsNullOrWhiteSpace(sub.Email))
        {
            emailAttempted = true;
            try
            {
                var (subject, html) = BuildOutreachEmail(outreach, request, sub, periodsOverride);
                emailDelivered = await _emailService.SendEmailAsync(sub.Email!, subject, html);
                if (!emailDelivered) emailError = "email-service-returned-false";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Outreach email dispatch threw for employee {EmployeeId} request {SubRequestId}",
                    sub.EmployeeId, request.SubRequestId);
                emailError = $"exception:{ex.GetType().Name}";
            }
        }

        if (!smsAttempted && !emailAttempted)
        {
            _logger.LogWarning(
                "Outreach dispatch had no eligible channels for employee {EmployeeId} (phone={HasPhone}, email={HasEmail}, smsEnabled={SmsEnabled}, optedOut={OptedOut}).",
                sub.EmployeeId,
                !string.IsNullOrWhiteSpace(sub.Phone),
                !string.IsNullOrWhiteSpace(sub.Email),
                _smsService.IsEnabled, sub.SmsOptedOut);
        }

        return new ChannelDispatchResult(
            SmsAttempted: smsAttempted, SmsDelivered: smsDelivered,
            SmsMessageId: smsMessageId, SmsError: smsError,
            EmailAttempted: emailAttempted, EmailDelivered: emailDelivered,
            EmailError: emailError);
    }

    private async Task<ChannelDispatchResult> TryDispatchConfirmationAsync(
        TcSubOutreach outreach, TcSubRequest request,
        string? acceptedPeriodsOverride = null)
    {
        var sub = outreach.SubEmployee;
        if (sub == null)
        {
            return new ChannelDispatchResult(false, false, null, null, false, false, null);
        }

        var smsEligible = _smsService.IsEnabled
                       && !sub.SmsOptedOut
                       && !string.IsNullOrWhiteSpace(sub.Phone);

        var smsAttempted = false;
        var smsDelivered = false;
        string? smsMessageId = null;
        string? smsError = null;

        if (smsEligible)
        {
            var body = BuildSmsConfirmationBody(request);
            var smsResult = await _smsService.SendAsync(sub.Phone!, body);
            smsAttempted = smsResult.Attempted;
            smsDelivered = smsResult.Delivered;
            smsMessageId = smsResult.MessageId;
            smsError = smsResult.ErrorReason;
        }

        var emailAttempted = false;
        var emailDelivered = false;
        string? emailError = null;
        if (!string.IsNullOrWhiteSpace(sub.Email))
        {
            emailAttempted = true;
            try
            {
                var (subject, html) = BuildConfirmationEmail(request, sub, acceptedPeriodsOverride);
                emailDelivered = await _emailService.SendEmailAsync(sub.Email, subject, html);
                if (!emailDelivered) emailError = "email-service-returned-false";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Confirmation email threw for employee {EmployeeId} request {SubRequestId}",
                    sub.EmployeeId, request.SubRequestId);
                emailError = $"exception:{ex.GetType().Name}";
            }
        }

        return new ChannelDispatchResult(
            SmsAttempted: smsAttempted, SmsDelivered: smsDelivered,
            SmsMessageId: smsMessageId, SmsError: smsError,
            EmailAttempted: emailAttempted, EmailDelivered: emailDelivered,
            EmailError: emailError);
    }

    // ── SMS body builders ────────────────────────────────────────────────

    private string BuildSmsOutreachBody(TcSubOutreach outreach, TcSubRequest request)
    {
        var baseUrl = _configuration["AzureCommunication:BaseUrl"]
                   ?? _configuration["App:BaseUrl"]
                   ?? "https://clock.newheightsed.com";

        var campusName = request.Campus?.CampusName ?? "New Heights";
        var dates = request.StartDate == request.EndDate
            ? request.StartDate.ToString("MMM d")
            : $"{request.StartDate:MMM d}-{request.EndDate:MMM d}";
        var periods = string.IsNullOrWhiteSpace(request.PeriodsNeeded) ? "TBD" : request.PeriodsNeeded;
        var link = $"{baseUrl.TrimEnd('/')}/sub/respond/{outreach.ResponseToken}";

        var body = $"New Heights: Sub request at {campusName} {dates}, {periods}. Accept or decline: {link} Reply STOP to opt out.";

        return body.Length <= SmsMaxLength ? body : body.Substring(0, SmsMaxLength);
    }

    private static string BuildSmsConfirmationBody(TcSubRequest request)
    {
        var campusName = request.Campus?.CampusName ?? "New Heights";
        var dates = request.StartDate == request.EndDate
            ? request.StartDate.ToString("MMM d")
            : $"{request.StartDate:MMM d}-{request.EndDate:MMM d}";
        var body = $"Confirmed! You are scheduled at New Heights {campusName} on {dates}. Check in with the front receptionist on arrival. Reply STOP to opt out.";
        return body.Length <= SmsMaxLength ? body : body.Substring(0, SmsMaxLength);
    }

    // ── Email body builders (refactored from Phase 5; no longer send themselves) ──

    private (string subject, string html) BuildOutreachEmail(
        TcSubOutreach outreach, TcSubRequest request, TcEmployee sub,
        string? periodsOverride = null)
    {
        var baseUrl = _configuration["AzureCommunication:BaseUrl"]
                   ?? _configuration["App:BaseUrl"]
                   ?? "https://clock.newheightsed.com";
        var link = $"{baseUrl.TrimEnd('/')}/sub/respond/{outreach.ResponseToken}";

        var subName = sub.Staff?.FirstName ?? sub.Staff?.FullName ?? "Substitute";
        var campusName = request.Campus?.CampusName ?? "New Heights";
        var teacher = request.RequestingEmployee?.Staff?.FullName ?? "—";
        var dateRange = request.StartDate == request.EndDate
            ? request.StartDate.ToString("dddd, MMM d, yyyy")
            : $"{request.StartDate:ddd MMM d} – {request.EndDate:ddd MMM d, yyyy}";

        // Phase A ext: emergency flag drives both the subject-line prefix
        // (so the sub can see URGENCY in their inbox list) and the red banner
        // inside the email body.
        var subject = request.IsEmergency
            ? $"URGENT — Same-day sub needed — {campusName} ({dateRange})"
            : $"New Heights sub assignment — {campusName} ({dateRange})";

        // Phase A: when dispatched after a partial accept, periodsOverride
        // contains only the remaining uncovered periods so the sub sees what
        // they can actually claim instead of the full original request.
        var periodsDisplay = !string.IsNullOrWhiteSpace(periodsOverride)
            ? periodsOverride
            : (request.PeriodsNeeded ?? "—");
        var partialNote = !string.IsNullOrWhiteSpace(periodsOverride)
            ? "<p style='background:#fef9c3;border:1px solid #fde68a;padding:.5rem .75rem;border-radius:4px;font-size:.9rem;'>" +
              "Note: earlier periods for this request are already covered by another sub. The periods below are what's still open." +
              "</p>"
            : "";

        var emergencyBanner = request.IsEmergency
            ? "<div style='background:#dc2626;color:white;padding:.65rem .9rem;border-radius:6px;margin:0 0 .75rem;font-weight:600;font-size:1rem;'>" +
              "&#9888;&#65039; URGENT — Same-day fill needed. This link expires in 30 minutes." +
              "</div>"
            : "";

        var html = $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  {emergencyBanner}
  <h2 style='color: #1d4ed8;'>New Heights Substitute Assignment</h2>
  <p>Hi {System.Net.WebUtility.HtmlEncode(subName)},</p>
  <p>You've been requested to substitute at:</p>
  {partialNote}
  <table style='border-collapse: collapse; margin: 0.5rem 0;'>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Campus:</td><td>{System.Net.WebUtility.HtmlEncode(campusName)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Dates:</td><td>{System.Net.WebUtility.HtmlEncode(dateRange)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Session:</td><td>{System.Net.WebUtility.HtmlEncode(request.SessionType ?? "Day")}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Periods:</td><td>{System.Net.WebUtility.HtmlEncode(periodsDisplay)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Subject:</td><td>{System.Net.WebUtility.HtmlEncode(request.SubjectArea ?? "—")}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Covering:</td><td>{System.Net.WebUtility.HtmlEncode(teacher)}</td></tr>
  </table>
  {(string.IsNullOrWhiteSpace(request.SpecialInstructions)
      ? ""
      : $"<p><strong>Instructions:</strong> {System.Net.WebUtility.HtmlEncode(request.SpecialInstructions)}</p>")}
  <p style='margin-top:1rem;'>
    <a href='{link}' style='display:inline-block;padding:10px 20px;background:#059669;color:#fff;text-decoration:none;border-radius:4px;font-weight:500;'>Open Assignment &amp; Respond</a>
  </p>
  <p style='color:#6b7280;font-size:0.85rem;'>
    This link expires on {outreach.TokenExpiresAt:dddd, MMM d} at {outreach.TokenExpiresAt:h:mm tt}.
    If you have questions, contact your campus manager.
  </p>
</div>";

        return (subject, html);
    }

    private static (string subject, string html) BuildConfirmationEmail(
        TcSubRequest request, TcEmployee sub, string? acceptedPeriodsOverride = null)
    {
        var subName = sub.Staff?.FirstName ?? sub.Staff?.FullName ?? "Substitute";
        var campusName = request.Campus?.CampusName ?? "New Heights";
        var dateRange = request.StartDate == request.EndDate
            ? request.StartDate.ToString("dddd, MMM d, yyyy")
            : $"{request.StartDate:ddd MMM d} – {request.EndDate:ddd MMM d, yyyy}";

        var subject = $"Confirmed — Sub assignment at {campusName} ({dateRange})";

        // Phase A: if the sub partially accepted, show only the periods they
        // committed to. Full accepts (or legacy whole-request accepts) fall
        // back to the request's full PeriodsNeeded.
        var periodsDisplay = !string.IsNullOrWhiteSpace(acceptedPeriodsOverride)
            ? acceptedPeriodsOverride
            : (request.PeriodsNeeded ?? "—");

        var html = $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  <h2 style='color: #059669;'>You're Confirmed</h2>
  <p>Hi {System.Net.WebUtility.HtmlEncode(subName)},</p>
  <p>Thanks for accepting! You're scheduled at:</p>
  <table style='border-collapse: collapse; margin: 0.5rem 0;'>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Campus:</td><td>{System.Net.WebUtility.HtmlEncode(campusName)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Dates:</td><td>{System.Net.WebUtility.HtmlEncode(dateRange)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Periods:</td><td>{System.Net.WebUtility.HtmlEncode(periodsDisplay)}</td></tr>
  </table>
  <p>Please check in with the front receptionist on arrival.</p>
  <p style='color:#6b7280;font-size:0.85rem;'>Questions? Contact your campus manager.</p>
</div>";

        return (subject, html);
    }

    // ── Stakeholder notifications (Phase 7d) ─────────────────────────────

    /// <summary>
    /// Fire-and-forget notifier: when a sub accepts or declines a request, send
    /// SMS (if enabled + recipient has phone + not SmsOptedOut) + email to:
    ///   (a) the supervisor who approved the absence (TcSubRequest.SupervisorApprovedBy)
    ///   (b) the requesting employee (TcSubRequest.RequestingEmployee)
    /// Reuses Phase 6 SMS+email parallel pattern. Never throws — notification
    /// failure must not roll back the accept/decline flow.
    /// </summary>
    private async Task TryNotifyStakeholdersAsync(TcSubOutreach outreach, TcSubRequest request, string eventKind)
    {
        try
        {
            using var context = await _dbFactory.CreateDbContextAsync();

            var sub = outreach.SubEmployee;
            var subName = sub?.Staff?.FullName ?? sub?.DisplayName ?? sub?.Email ?? "The substitute";
            var teacher = request.RequestingEmployee;
            var teacherName = teacher?.Staff?.FullName ?? teacher?.DisplayName ?? teacher?.Email ?? "the teacher";
            var campusName = request.Campus?.CampusName ?? "New Heights";
            var dates = request.StartDate == request.EndDate
                ? request.StartDate.ToString("MMM d")
                : $"{request.StartDate:MMM d}-{request.EndDate:MMM d}";

            // Resolve supervisor's employee record (for phone + opt-out).
            // Primary: SupervisorApprovedBy email, set when a supervisor formally
            // approved the absence (pre-9a flow, or post-9a after final approval).
            // Fallback (Phase 9a): the teacher's Entra-linked supervisor, populated
            // by EmployeeSyncService from the Entra `manager` attribute. In 9a
            // SupervisorApprovedBy isn't set until *after* the sub accepts, so we
            // need the fallback to notify the admin that final approval is pending.
            TcEmployee? supervisorEmp = null;
            if (!string.IsNullOrWhiteSpace(request.SupervisorApprovedBy))
            {
                var supervisorEmail = request.SupervisorApprovedBy.Trim();
                supervisorEmp = await context.TcEmployees
                    .AsNoTracking()
                    .Include(e => e.Staff)
                    .FirstOrDefaultAsync(e => e.Email == supervisorEmail && e.IsActive);
            }

            if (supervisorEmp == null)
            {
                var teacherId = request.RequestingEmployeeId;
                supervisorEmp = await context.TcEmployees
                    .AsNoTracking()
                    .Include(e => e.Staff)
                    .Where(e => e.IsActive)
                    .FirstOrDefaultAsync(e => context.TcEmployees
                        .Any(t => t.EmployeeId == teacherId
                               && t.SupervisorEmployeeId == e.EmployeeId));
            }

            var isAccept = string.Equals(eventKind, "ACCEPTED", StringComparison.OrdinalIgnoreCase);

            // Supervisor notification
            if (supervisorEmp != null)
            {
                var (smsBody, subject, html) = BuildStakeholderContent(
                    eventKind, isSupervisor: true,
                    subName, teacherName, campusName, dates, request);

                await TrySendOneStakeholderAsync(supervisorEmp, smsBody, subject, html);
            }

            // Requesting employee notification
            if (teacher != null && teacher.EmployeeId != outreach.SubEmployeeId)
            {
                var (smsBody, subject, html) = BuildStakeholderContent(
                    eventKind, isSupervisor: false,
                    subName, teacherName, campusName, dates, request);

                await TrySendOneStakeholderAsync(teacher, smsBody, subject, html);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "TryNotifyStakeholdersAsync: failed to notify stakeholders for request {SubRequestId} event {Event}. Accept/decline already recorded.",
                request.SubRequestId, eventKind);
        }
    }

    private async Task TrySendOneStakeholderAsync(TcEmployee recipient, string smsBody, string subject, string html)
    {
        if (_smsService.IsEnabled
         && !recipient.SmsOptedOut
         && !string.IsNullOrWhiteSpace(recipient.Phone))
        {
            try
            {
                await _smsService.SendAsync(recipient.Phone!, smsBody);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Stakeholder SMS send failed for {EmployeeId}", recipient.EmployeeId);
            }
        }

        if (!string.IsNullOrWhiteSpace(recipient.Email))
        {
            try
            {
                await _emailService.SendEmailAsync(recipient.Email!, subject, html);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Stakeholder email send failed for {EmployeeId}", recipient.EmployeeId);
            }
        }
    }

    private static (string smsBody, string subject, string html) BuildStakeholderContent(
        string eventKind, bool isSupervisor,
        string subName, string teacherName, string campusName, string dates,
        TcSubRequest request)
    {
        var isAccept = string.Equals(eventKind, "ACCEPTED", StringComparison.OrdinalIgnoreCase);

        string smsBody, subject, color, headline, body;

        if (isAccept && isSupervisor)
        {
            smsBody  = $"New Heights: {subName} accepted for {teacherName} at {campusName} on {dates}. Please give final approval: clock.newheightsed.com/supervisor/sub-requests";
            subject  = $"Action needed: final approval \u2014 {subName} for {teacherName} ({dates})";
            color    = "#059669";
            headline = "Substitute Confirmed \u2014 Final Approval Needed";
            body     = $"<p><strong>{System.Net.WebUtility.HtmlEncode(subName)}</strong> accepted the sub request for <strong>{System.Net.WebUtility.HtmlEncode(teacherName)}</strong> at <strong>{System.Net.WebUtility.HtmlEncode(campusName)}</strong> on <strong>{System.Net.WebUtility.HtmlEncode(dates)}</strong>.</p><p><strong>Next step:</strong> review and give final approval so the absence is officially confirmed.</p><p><a href='https://clock.newheightsed.com/supervisor/sub-requests' style='display:inline-block;padding:10px 20px;background:#2563eb;color:#fff;text-decoration:none;border-radius:4px;font-weight:500;'>Open Sub Requests</a></p>";
        }
        else if (isAccept && !isSupervisor)
        {
            smsBody  = $"New Heights: Good news — {subName} is confirmed as your sub for {dates} at {campusName}.";
            subject  = $"Your sub is confirmed — {subName} ({dates})";
            color    = "#059669";
            headline = "Your Substitute is Confirmed";
            body     = $"<p>Good news! <strong>{System.Net.WebUtility.HtmlEncode(subName)}</strong> has accepted your sub request for <strong>{System.Net.WebUtility.HtmlEncode(dates)}</strong> at <strong>{System.Net.WebUtility.HtmlEncode(campusName)}</strong>.</p>";
        }
        else if (!isAccept && isSupervisor)
        {
            smsBody  = $"New Heights: {subName} declined the sub request for {teacherName} on {dates}. The system will try the next sub in the queue if auto-cascade is enabled.";
            subject  = $"Sub declined — {subName} for {teacherName} ({dates})";
            color    = "#b45309";
            headline = "Substitute Declined";
            body     = $"<p><strong>{System.Net.WebUtility.HtmlEncode(subName)}</strong> declined the sub request for <strong>{System.Net.WebUtility.HtmlEncode(teacherName)}</strong> on <strong>{System.Net.WebUtility.HtmlEncode(dates)}</strong>.</p><p>If auto-cascade is enabled, the next queued sub has been contacted automatically. Otherwise, open Sub Requests to pick another sub.</p>";
        }
        else
        {
            smsBody  = $"New Heights: {subName} declined your sub request. We are looking for another sub for {dates}.";
            subject  = $"Sub request update — {subName} declined ({dates})";
            color    = "#b45309";
            headline = "Still Looking for a Sub";
            body     = $"<p><strong>{System.Net.WebUtility.HtmlEncode(subName)}</strong> declined your sub request. We are contacting another substitute for <strong>{System.Net.WebUtility.HtmlEncode(dates)}</strong>.</p><p>You will receive another update when a sub confirms.</p>";
        }

        // Keep SMS under ~320 chars (2 segments).
        if (smsBody.Length > 320) smsBody = smsBody.Substring(0, 320);

        var html = $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  <h2 style='color: {color};'>{headline}</h2>
  {body}
  <table style='border-collapse: collapse; margin: 0.5rem 0;'>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Campus:</td><td>{System.Net.WebUtility.HtmlEncode(campusName)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Dates:</td><td>{System.Net.WebUtility.HtmlEncode(dates)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Periods:</td><td>{System.Net.WebUtility.HtmlEncode(request.PeriodsNeeded ?? "—")}</td></tr>
  </table>
  <p style='color:#6b7280;font-size:0.85rem;'>You received this because you are listed as the {(isSupervisor ? "campus manager" : "requesting employee")} for this sub request.</p>
</div>";

        return (smsBody, subject, html);
    }

    // ── Absence decision notifications (Phase 7d gap close, post-9a) ─────

    public async Task NotifyAbsenceDecisionAsync(
        long subRequestId, string decision, string? reason, string adminEmail)
    {
        try
        {
            using var context = await _dbFactory.CreateDbContextAsync();
            var request = await context.TcSubRequests
                .AsNoTracking()
                .Include(r => r.RequestingEmployee).ThenInclude(e => e.Staff)
                .Include(r => r.Campus)
                .Include(r => r.AssignedSubEmployee).ThenInclude(e => e!.Staff)
                .FirstOrDefaultAsync(r => r.SubRequestId == subRequestId);

            if (request == null)
            {
                _logger.LogWarning(
                    "NotifyAbsenceDecisionAsync: request {Id} not found \u2014 skipping.",
                    subRequestId);
                return;
            }

            var isApproved = string.Equals(decision, "APPROVED", StringComparison.OrdinalIgnoreCase);

            var teacher = request.RequestingEmployee;
            var sub = request.AssignedSubEmployee;
            var teacherShort = teacher?.Staff?.FirstName ?? teacher?.Staff?.FullName ?? teacher?.DisplayName ?? "Teacher";
            var teacherFull  = teacher?.Staff?.FullName ?? teacher?.DisplayName ?? teacher?.Email ?? "the teacher";
            var subName = sub?.Staff?.FullName ?? sub?.DisplayName ?? sub?.Email ?? "a substitute";
            var campusName = request.Campus?.CampusName ?? "New Heights";
            var dates = request.StartDate == request.EndDate
                ? request.StartDate.ToString("MMM d")
                : $"{request.StartDate:MMM d}-{request.EndDate:MMM d}";

            // Teacher notification (always)
            if (teacher != null)
            {
                // Migration 050: approval emails get a deep link to the class
                // notes page so teachers can add lesson plans / URLs the sub
                // needs before the absence. Denied emails don't get the link
                // since the absence isn't happening.
                var classNotesUrl = isApproved
                    ? BuildClassNotesUrl(subRequestId)
                    : null;

                var (smsBody, subject, html) = BuildAbsenceDecisionForTeacher(
                    isApproved, teacherShort, subName, campusName, dates, reason,
                    request.AssignedSubEmployeeId.HasValue,
                    classNotesUrl);
                await TrySendOneStakeholderAsync(teacher, smsBody, subject, html);
            }

            // Sub notification only on DENY when a sub was previously confirmed.
            if (!isApproved && sub != null)
            {
                var (smsBody, subject, html) = BuildAbsenceDeniedForSub(
                    teacherFull, campusName, dates, reason);
                await TrySendOneStakeholderAsync(sub, smsBody, subject, html);
            }

            _logger.LogInformation(
                "NotifyAbsenceDecisionAsync: request {Id} decision {Decision} by {Admin} \u2014 teacher notified={TeacherOk}, sub notified={SubOk}.",
                subRequestId, decision, adminEmail,
                teacher != null, !isApproved && sub != null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "NotifyAbsenceDecisionAsync: notification failed for request {Id} decision {Decision}. The decision itself is already recorded in TcSubRequest + audit log.",
                subRequestId, decision);
        }
    }

    /// <summary>
    /// Builds the absolute URL to the class-notes page. Uses the same base
    /// URL resolution as outreach emails so the deep link reaches production
    /// regardless of what host the background service is running on.
    /// </summary>
    private string BuildClassNotesUrl(long subRequestId)
    {
        var baseUrl = _configuration["AzureCommunication:BaseUrl"]
                   ?? _configuration["App:BaseUrl"]
                   ?? "https://clock.newheightsed.com";
        return $"{baseUrl.TrimEnd('/')}/sub-request/{subRequestId}/notes";
    }

    private static (string smsBody, string subject, string html) BuildAbsenceDecisionForTeacher(
        bool isApproved, string teacherShort, string subName, string campusName,
        string dates, string? reason, bool hadAssignedSub,
        string? classNotesUrl = null)
    {
        string smsBody, subject, color, headline, body;
        if (isApproved)
        {
            // Phase A ext: remind the teacher that this app only tracks the
            // substitute — actual time-off / PTO / sick leave must still be
            // filed in Ascender. Included in both SMS (short form) and email
            // (prominent yellow callout) so the teacher can't miss it.
            smsBody  = hadAssignedSub
                ? $"New Heights: Your absence for {dates} at {campusName} is approved. {subName} is confirmed. REMINDER: submit your time-off request in Ascender."
                : $"New Heights: Your absence for {dates} at {campusName} is approved. REMINDER: submit your time-off request in Ascender.";
            subject  = $"Absence approved \u2014 {campusName} ({dates})";
            color    = "#059669";
            headline = "Your Absence is Approved";
            body     = $"<p>Your absence request for <strong>{System.Net.WebUtility.HtmlEncode(dates)}</strong> at <strong>{System.Net.WebUtility.HtmlEncode(campusName)}</strong> has been <strong>approved</strong>.</p>";
            if (hadAssignedSub)
                body += $"<p><strong>{System.Net.WebUtility.HtmlEncode(subName)}</strong> is confirmed as your sub.</p>";
            body += @"
<div style='margin-top:1rem; padding:.85rem 1rem; background:#fffbeb; border:1px solid #fde68a; border-left:4px solid #f5b81c; border-radius:6px;'>
  <strong style='color:#92400e;'>Reminder: file your time-off request in Ascender</strong>
  <p style='margin:.35rem 0 0; color:#78350f;'>
    This approval covers your substitute assignment in the TimeClock system. To
    officially record your leave with HR — PTO, sick, personal, vacation, or
    professional development — please log in to <strong>Ascender</strong> and
    submit a separate time-off request for these dates.
  </p>
</div>";

            // Migration 050: link to the class-notes page so the teacher can
            // leave lesson plans and paste-link URLs for the sub to find.
            if (!string.IsNullOrWhiteSpace(classNotesUrl))
            {
                var encodedUrl = System.Net.WebUtility.HtmlEncode(classNotesUrl);
                body += $@"
<div style='margin-top:1rem; padding:.85rem 1rem; background:#eff6ff; border:1px solid #bfdbfe; border-left:4px solid #1e3a5f; border-radius:6px;'>
  <strong style='color:#1e3a5f;'>Next: leave class notes for your sub</strong>
  <p style='margin:.35rem 0 .65rem; color:#1e3a5f;'>
    Add lesson plans, seating notes, and links to any files the sub will need.
    You can paste cloud-drive URLs (OneDrive, Google Drive, SharePoint). Both
    your substitute(s) and supervisor will be able to see this page.
  </p>
  <a href='{encodedUrl}' style='display:inline-block; padding:.55rem 1.1rem; background:#1e3a5f; color:#fff; text-decoration:none; border-radius:6px; font-weight:600;'>Open Class Notes</a>
</div>";
            }
        }
        else
        {
            smsBody = $"New Heights: Your absence for {dates} at {campusName} was denied.";
            if (!string.IsNullOrWhiteSpace(reason))
                smsBody += $" Reason: {reason}";
            subject  = $"Absence denied \u2014 {campusName} ({dates})";
            color    = "#b45309";
            headline = "Your Absence was Denied";
            body     = $"<p>Your absence request for <strong>{System.Net.WebUtility.HtmlEncode(dates)}</strong> at <strong>{System.Net.WebUtility.HtmlEncode(campusName)}</strong> has been <strong>denied</strong> by admin.</p>";
            if (!string.IsNullOrWhiteSpace(reason))
                body += $"<p><strong>Reason:</strong> {System.Net.WebUtility.HtmlEncode(reason)}</p>";
            body += "<p>If you have questions, please reach out to your campus manager.</p>";
        }
        if (smsBody.Length > SmsMaxLength) smsBody = smsBody.Substring(0, SmsMaxLength);

        var html = $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  <h2 style='color: {color};'>{headline}</h2>
  {body}
  <p style='color:#6b7280;font-size:0.85rem;'>You received this because you are the requesting employee for this sub request.</p>
</div>";
        return (smsBody, subject, html);
    }

    private static (string smsBody, string subject, string html) BuildAbsenceDeniedForSub(
        string teacherFull, string campusName, string dates, string? reason)
    {
        var smsBody = $"New Heights: The sub assignment for {teacherFull} at {campusName} on {dates} has been canceled by admin. You do not need to report for this day.";
        if (smsBody.Length > SmsMaxLength) smsBody = smsBody.Substring(0, SmsMaxLength);

        var subject = $"Sub assignment canceled \u2014 {teacherFull} ({dates})";
        var headline = "Sub Assignment Canceled";
        var body = $"<p>The sub request from <strong>{System.Net.WebUtility.HtmlEncode(teacherFull)}</strong> at <strong>{System.Net.WebUtility.HtmlEncode(campusName)}</strong> on <strong>{System.Net.WebUtility.HtmlEncode(dates)}</strong> has been <strong>denied</strong> by admin. You do not need to report for this day.</p>";
        if (!string.IsNullOrWhiteSpace(reason))
            body += $"<p><strong>Reason provided:</strong> {System.Net.WebUtility.HtmlEncode(reason)}</p>";

        var html = $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  <h2 style='color: #b45309;'>{headline}</h2>
  {body}
  <p style='color:#6b7280;font-size:0.85rem;'>You received this because you were the assigned substitute for this request.</p>
</div>";
        return (smsBody, subject, html);
    }

    // ── Token ────────────────────────────────────────────────────────────

    private static string GenerateToken()
    {
        var bytes = new byte[TokenByteLength];
        RandomNumberGenerator.Fill(bytes);
        // URL-safe base64. 48 bytes -> 64 chars.
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
