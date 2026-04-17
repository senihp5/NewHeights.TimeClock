using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Writes entries to TC_AuditLog. Callers pass an AuditEntry and the service
/// fills in user context and IP from the current HTTP request automatically,
/// or the caller can set them explicitly for background work.
///
/// This service is fault-tolerant by design — a failed audit write logs a warning
/// but does NOT throw. We never want audit failures to interrupt user operations.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Low-level write. Caller fills AuditEntry fields. Missing user/IP
    /// values are populated from the current request context.
    /// </summary>
    Task LogAsync(AuditEntry entry, CancellationToken ct = default);

    /// <summary>
    /// Convenience wrapper — most callers should use this. OldValues/NewValues
    /// are serialized to JSON automatically if non-null.
    /// </summary>
    Task LogActionAsync(
        string actionCode,
        string entityType,
        string entityId,
        object? oldValues = null,
        object? newValues = null,
        string? deltaSummary = null,
        string? reason = null,
        string source = AuditSource.AdminUi,
        int? employeeId = null,
        int? campusId = null,
        long? punchId = null,
        long? correctionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// All audit rows for a specific entity (e.g. every event on SubRequest #42).
    /// </summary>
    Task<List<TcAuditLog>> GetHistoryAsync(string entityType, string entityId, CancellationToken ct = default);

    /// <summary>
    /// All audit rows for a specific employee across entity types, optionally
    /// filtered by date range.
    /// </summary>
    Task<List<TcAuditLog>> GetEmployeeHistoryAsync(
        int employeeId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default);

    /// <summary>
    /// Filtered audit query for the /admin/audit page + inline entity panels (Phase 7b).
    /// All filters optional. Results are capped at <paramref name="maxRows"/> to protect
    /// the UI from pathological queries; the caller should tighten the date range or
    /// narrow filters if the cap is hit.
    /// </summary>
    /// <param name="from">Inclusive lower bound on CreatedDate. Null = unbounded.</param>
    /// <param name="to">Inclusive upper bound on CreatedDate. Null = unbounded.</param>
    /// <param name="actionCodes">If non-empty, only rows with ActionCode IN this set are returned.</param>
    /// <param name="entityTypes">If non-empty, only rows with EntityType IN this set are returned.</param>
    /// <param name="userQuery">Optional substring match against UserName / UserEmail (case-insensitive).</param>
    /// <param name="maxRows">Hard cap on returned rows. Default 500. Ordered CreatedDate DESC so newest come first.</param>
    Task<List<TcAuditLog>> GetFilteredHistoryAsync(
        DateTime? from = null,
        DateTime? to = null,
        IReadOnlyList<string>? actionCodes = null,
        IReadOnlyList<string>? entityTypes = null,
        string? userQuery = null,
        int maxRows = 500,
        CancellationToken ct = default);
}

public class AuditService : IAuditService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly IUserContextService _userContext;
    private readonly IHttpContextAccessor _httpContext;
    private readonly ILogger<AuditService> _logger;

    // JSON options — ignore nulls, handle cycles gracefully, don't explode on self-refs.
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        ReferenceHandler = System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public AuditService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IUserContextService userContext,
        IHttpContextAccessor httpContext,
        ILogger<AuditService> logger)
    {
        _dbFactory = dbFactory;
        _userContext = userContext;
        _httpContext = httpContext;
        _logger = logger;
    }

    public async Task LogAsync(AuditEntry entry, CancellationToken ct = default)
    {
        try
        {
            var row = await BuildRowAsync(entry);

            // Use a separate DbContext so the audit write lives in its own unit of
            // work — failures or caller rollbacks do not discard the audit row.
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            db.TcAuditLogs.Add(row);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Never let audit failures crash the caller.
            _logger.LogWarning(ex,
                "Audit write failed for action {ActionCode} on {EntityType}/{EntityId}",
                entry.ActionCode, entry.EntityType, entry.EntityId);
        }
    }

    public Task LogActionAsync(
        string actionCode,
        string entityType,
        string entityId,
        object? oldValues = null,
        object? newValues = null,
        string? deltaSummary = null,
        string? reason = null,
        string source = AuditSource.AdminUi,
        int? employeeId = null,
        int? campusId = null,
        long? punchId = null,
        long? correctionId = null,
        CancellationToken ct = default)
    {
        var entry = new AuditEntry
        {
            ActionCode   = actionCode,
            EntityType   = entityType,
            EntityId     = entityId,
            OldValues    = oldValues,
            NewValues    = newValues,
            DeltaSummary = deltaSummary,
            Reason       = reason,
            Source       = source,
            EmployeeId   = employeeId,
            CampusId     = campusId,
            PunchId      = punchId,
            CorrectionId = correctionId
        };
        return LogAsync(entry, ct);
    }

    public async Task<List<TcAuditLog>> GetHistoryAsync(
        string entityType, string entityId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.TcAuditLogs
            .Where(a => a.EntityType == entityType && a.EntityId == entityId)
            .OrderByDescending(a => a.CreatedDate)
            .ToListAsync(ct);
    }

    public async Task<List<TcAuditLog>> GetEmployeeHistoryAsync(
        int employeeId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.TcAuditLogs.Where(a => a.EmployeeId == employeeId);

        if (from.HasValue) q = q.Where(a => a.CreatedDate >= from.Value);
        if (to.HasValue)   q = q.Where(a => a.CreatedDate <= to.Value);

        return await q.OrderByDescending(a => a.CreatedDate).ToListAsync(ct);
    }

    public async Task<List<TcAuditLog>> GetFilteredHistoryAsync(
        DateTime? from = null,
        DateTime? to = null,
        IReadOnlyList<string>? actionCodes = null,
        IReadOnlyList<string>? entityTypes = null,
        string? userQuery = null,
        int maxRows = 500,
        CancellationToken ct = default)
    {
        if (maxRows <= 0) maxRows = 500;
        if (maxRows > 5000) maxRows = 5000; // hard ceiling

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.TcAuditLogs.AsNoTracking().AsQueryable();

        if (from.HasValue) q = q.Where(a => a.CreatedDate >= from.Value);
        if (to.HasValue)   q = q.Where(a => a.CreatedDate <= to.Value);

        if (actionCodes != null && actionCodes.Count > 0)
        {
            // Materialize to avoid IN-clause translation quirks with IReadOnlyList.
            var codes = actionCodes.ToArray();
            q = q.Where(a => codes.Contains(a.ActionCode));
        }

        if (entityTypes != null && entityTypes.Count > 0)
        {
            var types = entityTypes.ToArray();
            q = q.Where(a => types.Contains(a.EntityType));
        }

        if (!string.IsNullOrWhiteSpace(userQuery))
        {
            var needle = userQuery.Trim();
            q = q.Where(a => EF.Functions.Like(a.UserName, $"%{needle}%")
                          || (a.UserEmail != null && EF.Functions.Like(a.UserEmail, $"%{needle}%")));
        }

        return await q
            .OrderByDescending(a => a.CreatedDate)
            .Take(maxRows)
            .ToListAsync(ct);
    }

    // Maps AuditEntry DTO -> TcAuditLog row, filling user + IP from context if missing.
    private async Task<TcAuditLog> BuildRowAsync(AuditEntry entry)
    {
        // Resolve user only if the caller didn't set it.
        string userId, userName;
        string? userEmail, userRole;

        if (!string.IsNullOrEmpty(entry.UserId))
        {
            userId    = entry.UserId;
            userName  = entry.UserName ?? "(unknown)";
            userEmail = entry.UserEmail;
            userRole  = entry.UserRole;
        }
        else
        {
            UserContext? ctx = null;
            try { ctx = await _userContext.GetCurrentUserAsync(); }
            catch
            {
                // Background services (AutoCheckoutService, IHostedService jobs) have
                // no AuthenticationState — that's expected, fall through to SYSTEM.
            }

            if (ctx?.IsAuthenticated == true)
            {
                userId    = ctx.EntraObjectId ?? ctx.Email;
                userName  = ctx.DisplayName;
                userEmail = ctx.Email;
                userRole  = ResolveRoleLabel(ctx);
            }
            else
            {
                userId    = "SYSTEM";
                userName  = "SYSTEM";
                userEmail = null;
                userRole  = null;
            }
        }

        // IP + session id only if the caller didn't set them.
        var ip        = entry.IpAddress ?? _httpContext.HttpContext?.Connection?.RemoteIpAddress?.ToString();
        var sessionId = entry.SessionId ?? _httpContext.HttpContext?.TraceIdentifier;

        return new TcAuditLog
        {
            ActionCode    = entry.ActionCode,
            UserId        = Trim(userId, 100),
            UserName      = Trim(userName, 150),
            UserEmail     = Trim(userEmail, 200),
            UserRole      = Trim(userRole, 30),
            EntityType    = entry.EntityType,
            EntityId      = entry.EntityId,
            PunchId       = entry.PunchId,
            CorrectionId  = entry.CorrectionId,
            EmployeeId    = entry.EmployeeId,
            CampusId      = entry.CampusId,
            OldValuesJson = SerializeOrNull(entry.OldValues),
            NewValuesJson = SerializeOrNull(entry.NewValues),
            DeltaSummary  = Trim(entry.DeltaSummary, 500),
            Reason        = Trim(entry.Reason, 500),
            Source        = entry.Source,
            IPAddress     = Trim(ip, 50),
            SessionId     = Trim(sessionId, 100),
            CreatedDate   = DateTime.Now  // Rule 4.1 — local time always.
        };
    }

    private static string? SerializeOrNull(object? value)
    {
        if (value is null) return null;
        if (value is string s) return s;
        try { return JsonSerializer.Serialize(value, _jsonOptions); }
        catch { return value.ToString(); }
    }

    // Most-specific role wins. This labels the audit row so compliance reports
    // can slice by role without re-joining Entra.
    private static string? ResolveRoleLabel(UserContext ctx)
    {
        if (ctx.IsAdmin)        return "Admin";
        if (ctx.IsHR)           return "HR";
        if (ctx.IsDistrict)     return "District";
        if (ctx.IsSupervisor)   return "Supervisor";
        if (ctx.IsCampusAdmin)  return "CampusAdmin";
        if (ctx.IsReception)    return "Reception";
        if (ctx.IsSubstitute)   return "Substitute";
        if (ctx.IsHourly)       return "Hourly";
        return null;
    }

    [return: NotNullIfNotNull(nameof(s))]
    private static string? Trim(string? s, int max)
    {
        if (s is null) return null;
        return s.Length <= max ? s : s.Substring(0, max);
    }
}
