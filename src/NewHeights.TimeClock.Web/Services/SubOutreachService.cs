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
    /// Validates the token, marks the outreach ACCEPTED, transitions the parent
    /// request to SubConfirmed with AssignedSubEmployeeId populated, fires a
    /// confirmation email, audits SUB_ACCEPTED + SUB_CONFIRMATION_SENT.
    /// Throws if token is invalid / expired / already responded.
    /// </summary>
    Task<TcSubOutreach> ProcessAcceptAsync(string token);

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
}

public class SubOutreachService : ISubOutreachService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly IAuditService _audit;
    private readonly IEmailService _emailService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SubOutreachService> _logger;

    // Tokens: 48 random bytes base64-encoded = 64 chars. Crypto random, URL-safe.
    private const int TokenByteLength = 48;
    private const int TokenValidityHours = 48;

    public SubOutreachService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IAuditService audit,
        IEmailService emailService,
        IConfiguration configuration,
        ILogger<SubOutreachService> logger)
    {
        _dbFactory = dbFactory;
        _audit = audit;
        _emailService = emailService;
        _configuration = configuration;
        _logger = logger;
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

        if (request.Status != SubRequestStatus.AbsenceApproved
            && request.Status != SubRequestStatus.SubAssigned)
        {
            throw new InvalidOperationException(
                $"Cannot send outreach — request is {request.Status}. Must be AbsenceApproved or SubAssigned.");
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

        var tokenExpiresAt = DateTime.Now.AddHours(TokenValidityHours);
        var outreachRows = new List<TcSubOutreach>();

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

            // First in the list sends immediately; rest are queued (auto-cascade).
            if (i == 0)
            {
                var sendOk = await TrySendOutreachEmailAsync(row, request, sub);
                row.MessageSentAt = DateTime.Now;
                row.DeliveryStatus = sendOk ? "DELIVERED" : "FAILED";
            }

            context.TcSubOutreach.Add(row);
            outreachRows.Add(row);
        }

        // First outreach for this request bumps status from AbsenceApproved → SubAssigned.
        if (request.Status == SubRequestStatus.AbsenceApproved)
        {
            request.Status = SubRequestStatus.SubAssigned;
            request.ModifiedDate = DateTime.Now;
        }

        await context.SaveChangesAsync();

        // Audit each *sent* outreach (not the queued ones).
        foreach (var row in outreachRows.Where(r => r.MessageSentAt.HasValue))
        {
            await _audit.LogActionAsync(
                actionCode: AuditActions.SubOutreach.SmsSent,
                entityType: AuditEntityTypes.SubOutreach,
                entityId: row.OutreachId.ToString(),
                newValues: new
                {
                    row.SubRequestId,
                    row.SubEmployeeId,
                    row.OutreachMethod,
                    row.SequenceOrder,
                    row.DeliveryStatus,
                    row.EmailAddress,
                    SentBy = sentByEmail,
                    row.TokenExpiresAt
                },
                deltaSummary: $"Sent {row.OutreachMethod} outreach (seq {row.SequenceOrder}) "
                            + $"to employee {row.SubEmployeeId} for sub request {subRequestId}",
                source: AuditSource.AdminUi,
                employeeId: row.SubEmployeeId);
        }

        return outreachRows;
    }

    // ── Accept ───────────────────────────────────────────────────────────

    public async Task<TcSubOutreach> ProcessAcceptAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("token is required.", nameof(token));

        using var context = await _dbFactory.CreateDbContextAsync();

        var outreach = await context.TcSubOutreach
            .Include(o => o.SubRequest).ThenInclude(r => r!.RequestingEmployee).ThenInclude(e => e!.Staff)
            .Include(o => o.SubRequest).ThenInclude(r => r!.Campus)
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

        outreach.ResponseStatus = "ACCEPTED";
        outreach.RespondedAt = DateTime.Now;

        var request = outreach.SubRequest!;
        request.AssignedSubEmployeeId = outreach.SubEmployeeId;
        request.AssignedDate = DateTime.Now;
        request.Status = SubRequestStatus.SubConfirmed;
        request.ModifiedDate = DateTime.Now;

        await context.SaveChangesAsync();

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubOutreach.Accepted,
            entityType: AuditEntityTypes.SubOutreach,
            entityId: outreach.OutreachId.ToString(),
            newValues: new
            {
                outreach.SubRequestId,
                outreach.SubEmployeeId,
                AcceptedAt = outreach.RespondedAt,
                RequestStatus = request.Status.ToString()
            },
            deltaSummary: $"Sub employee {outreach.SubEmployeeId} accepted sub request {outreach.SubRequestId}",
            source: AuditSource.PublicLink,
            employeeId: outreach.SubEmployeeId);

        // Confirmation email to the sub (+ eventual supervisor notification).
        var confirmOk = await TrySendConfirmationEmailAsync(outreach, request);
        if (confirmOk)
        {
            request.ConfirmationSentAt = DateTime.Now;
            await context.SaveChangesAsync();

            await _audit.LogActionAsync(
                actionCode: AuditActions.SubOutreach.ConfirmationSent,
                entityType: AuditEntityTypes.SubOutreach,
                entityId: outreach.OutreachId.ToString(),
                deltaSummary: $"Sent confirmation email to employee {outreach.SubEmployeeId} for sub request {outreach.SubRequestId}",
                source: AuditSource.System,
                employeeId: outreach.SubEmployeeId);
        }

        return outreach;
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
            // Revert request so supervisor can pick another sub.
            if (request.Status == SubRequestStatus.SubAssigned)
            {
                request.Status = SubRequestStatus.AbsenceApproved;
                request.ModifiedDate = DateTime.Now;
                await context.SaveChangesAsync();
            }
            return;
        }

        // Refresh token expiry for this send, then dispatch.
        nextQueued.TokenExpiresAt = DateTime.Now.AddHours(TokenValidityHours);
        var sendOk = await TrySendOutreachEmailAsync(nextQueued, request, nextQueued.SubEmployee!);
        nextQueued.MessageSentAt = DateTime.Now;
        nextQueued.DeliveryStatus = sendOk ? "DELIVERED" : "FAILED";
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

        await _audit.LogActionAsync(
            actionCode: AuditActions.SubOutreach.SmsSent,
            entityType: AuditEntityTypes.SubOutreach,
            entityId: nextQueued.OutreachId.ToString(),
            newValues: new
            {
                nextQueued.SubRequestId,
                nextQueued.SubEmployeeId,
                nextQueued.OutreachMethod,
                nextQueued.SequenceOrder,
                nextQueued.DeliveryStatus,
                nextQueued.EmailAddress,
                nextQueued.TokenExpiresAt
            },
            deltaSummary: $"Sent cascaded {nextQueued.OutreachMethod} outreach (seq {nextQueued.SequenceOrder}) "
                        + $"to employee {nextQueued.SubEmployeeId} for sub request {subRequestId}",
            source: AuditSource.System,
            employeeId: nextQueued.SubEmployeeId);
    }

    // ── Email templates ──────────────────────────────────────────────────

    private async Task<bool> TrySendOutreachEmailAsync(
        TcSubOutreach outreach, TcSubRequest request, TcEmployee sub)
    {
        if (string.IsNullOrWhiteSpace(sub.Email))
        {
            _logger.LogWarning(
                "Cannot send outreach email — employee {EmployeeId} has no email on record.",
                sub.EmployeeId);
            return false;
        }

        var baseUrl = _configuration["App:BaseUrl"] ?? "https://clock.newheightsed.com";
        var link = $"{baseUrl.TrimEnd('/')}/sub/respond/{outreach.ResponseToken}";

        var subName = sub.Staff?.FirstName ?? sub.Staff?.FullName ?? "Substitute";
        var campusName = request.Campus?.CampusName ?? "New Heights";
        var teacher = request.RequestingEmployee?.Staff?.FullName ?? "—";
        var dateRange = request.StartDate == request.EndDate
            ? request.StartDate.ToString("dddd, MMM d, yyyy")
            : $"{request.StartDate:ddd MMM d} – {request.EndDate:ddd MMM d, yyyy}";

        var subject = $"New Heights sub assignment — {campusName} ({dateRange})";

        var html = $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  <h2 style='color: #1d4ed8;'>New Heights Substitute Assignment</h2>
  <p>Hi {System.Net.WebUtility.HtmlEncode(subName)},</p>
  <p>You've been requested to substitute at:</p>
  <table style='border-collapse: collapse; margin: 0.5rem 0;'>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Campus:</td><td>{System.Net.WebUtility.HtmlEncode(campusName)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Dates:</td><td>{System.Net.WebUtility.HtmlEncode(dateRange)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Session:</td><td>{System.Net.WebUtility.HtmlEncode(request.SessionType ?? "Day")}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Periods:</td><td>{System.Net.WebUtility.HtmlEncode(request.PeriodsNeeded ?? "—")}</td></tr>
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

        try
        {
            var ok = await _emailService.SendEmailAsync(sub.Email, subject, html);
            if (!ok)
            {
                _logger.LogWarning(
                    "Outreach email send returned false for employee {EmployeeId} request {SubRequestId}",
                    sub.EmployeeId, request.SubRequestId);
            }
            return ok;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Outreach email send threw for employee {EmployeeId} request {SubRequestId}",
                sub.EmployeeId, request.SubRequestId);
            return false;
        }
    }

    private async Task<bool> TrySendConfirmationEmailAsync(TcSubOutreach outreach, TcSubRequest request)
    {
        var sub = outreach.SubEmployee;
        if (sub == null || string.IsNullOrWhiteSpace(sub.Email))
        {
            _logger.LogWarning(
                "Cannot send confirmation email — employee {EmployeeId} has no email on record.",
                outreach.SubEmployeeId);
            return false;
        }

        var subName = sub.Staff?.FirstName ?? sub.Staff?.FullName ?? "Substitute";
        var campusName = request.Campus?.CampusName ?? "New Heights";
        var dateRange = request.StartDate == request.EndDate
            ? request.StartDate.ToString("dddd, MMM d, yyyy")
            : $"{request.StartDate:ddd MMM d} – {request.EndDate:ddd MMM d, yyyy}";

        var subject = $"Confirmed — Sub assignment at {campusName} ({dateRange})";

        var html = $@"
<div style='font-family: Segoe UI, Arial, sans-serif; max-width: 600px; color: #1f2937;'>
  <h2 style='color: #059669;'>You're Confirmed</h2>
  <p>Hi {System.Net.WebUtility.HtmlEncode(subName)},</p>
  <p>Thanks for accepting! You're scheduled at:</p>
  <table style='border-collapse: collapse; margin: 0.5rem 0;'>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Campus:</td><td>{System.Net.WebUtility.HtmlEncode(campusName)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Dates:</td><td>{System.Net.WebUtility.HtmlEncode(dateRange)}</td></tr>
    <tr><td style='padding:4px 12px 4px 0;font-weight:600;'>Periods:</td><td>{System.Net.WebUtility.HtmlEncode(request.PeriodsNeeded ?? "—")}</td></tr>
  </table>
  <p>Please check in with the front receptionist on arrival.</p>
  <p style='color:#6b7280;font-size:0.85rem;'>Questions? Contact your campus manager.</p>
</div>";

        try
        {
            return await _emailService.SendEmailAsync(sub.Email, subject, html);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Confirmation email send threw for employee {EmployeeId} request {SubRequestId}",
                outreach.SubEmployeeId, request.SubRequestId);
            return false;
        }
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
