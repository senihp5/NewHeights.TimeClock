using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Shared.Constants;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Reads the current user's identity, roles, and campus from Entra ID token claims.
///
/// CAMPUS RESOLUTION — reads three Entra properties, priority order:
///   1. department claim  ("NHSS" | "NHM" | "District")  — PRIMARY for logic
///   2. officeLocation claim ("New Heights - Stop Six" | "New Heights - McCart" | "District Office")
///      — used for display label; also used as fallback if department is missing
///   3. manager — NOT a token claim; requires Graph API. The supervisor relationship
///      is stored in TC_Employees.SupervisorEmployeeId instead. Use the Entra manager
///      property during employee import/sync to populate that DB column, not at runtime.
///
/// ENTRA SETUP (one-time per user):
///   - Set Department to:     NHSS | NHM | District
///   - Set Office Location to: New Heights - Stop Six | New Heights - McCart | District Office
///   - Set Manager to:         supervisor's Entra account (for HR governance + future sync)
///
/// APP REGISTRATION (one-time):
///   Token configuration → Add optional claim → ID + Access → select "department"
///   Token configuration → Add optional claim → ID + Access → select "office_location"
///   No Graph API calls needed at runtime.
/// </summary>
public interface IUserContextService
{
    Task<UserContext> GetCurrentUserAsync();
}

public class UserContext
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? EntraObjectId { get; set; }

    // Campus code resolved from department claim (primary) or officeLocation (fallback)
    // Values: AppConstants.Campus.StopSixCode ("STOP6") | McCartCode ("MCCART") | null (District)
    public string? CampusCode { get; set; }

    // Human-readable label from officeLocation claim — for UI display only
    // Values: "New Heights - Stop Six" | "New Heights - McCart" | "District Office" | null
    public string? OfficeLocation { get; set; }

    // Raw department value from token — "NHSS" | "NHM" | "District"
    public string? Department { get; set; }

    // Role flags derived from token role claims
    public bool IsAdmin { get; set; }
    public bool IsHR { get; set; }
    public bool IsSupervisor { get; set; }
    public bool IsHourly { get; set; }
    public bool IsSubstitute { get; set; }
    public bool IsCampusAdmin { get; set; }
    public bool IsAllStaff { get; set; }
    public bool IsReception { get; set; }
    public bool IsDistrict { get; set; }

    public bool IsAuthenticated { get; set; }

    // Resolved campus DB integer ID from campus code
    public int? CampusId => CampusCode switch
    {
        AppConstants.Campus.StopSixCode => AppConstants.Campus.StopSixPowerSchoolId,
        AppConstants.Campus.McCartCode  => AppConstants.Campus.McCartPowerSchoolId,
        _ => null
    };

    // Friendly campus name for UI display
    public string CampusDisplayName => CampusCode switch
    {
        AppConstants.Campus.StopSixCode => "Stop Six",
        AppConstants.Campus.McCartCode  => "McCart",
        _ => "District"
    };
}

public class UserContextService : IUserContextService
{
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly ILogger<UserContextService> _logger;

    public UserContextService(
        AuthenticationStateProvider authStateProvider,
        IDbContextFactory<TimeClockDbContext> dbFactory,
        ILogger<UserContextService> logger)
    {
        _authStateProvider = authStateProvider;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<UserContext> GetCurrentUserAsync()
    {
        var authState = await _authStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;

        var ctx = new UserContext
        {
            IsAuthenticated = user.Identity?.IsAuthenticated == true
        };

        if (!ctx.IsAuthenticated)
            return ctx;

        ctx.Email = user.FindFirst("preferred_username")?.Value
                 ?? user.FindFirst(ClaimTypes.Email)?.Value
                 ?? "";
        ctx.DisplayName = user.FindFirst("name")?.Value
                       ?? user.Identity?.Name
                       ?? "";
        ctx.EntraObjectId = user.FindFirst("oid")?.Value
                         ?? user.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

        // Read both properties from token claims.
        // When source:"user" is set in the manifest, JWT emits these as
        // "department" and "officeLocation" (camelCase, no underscores).
        ctx.Department     = user.FindFirst("department")?.Value;
        ctx.OfficeLocation = user.FindFirst("officeLocation")?.Value
                          ?? user.FindFirst("office_location")?.Value
                          ?? user.FindFirst("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/locality")?.Value;

        // Resolve campus code: department is primary, officeLocation is fallback
        ctx.CampusCode = ResolveCampusCode(ctx.Department, ctx.OfficeLocation);

        // Phase post-smoke-test: if the Entra claims don't resolve a campus
        // (department / officeLocation are blank or unrecognized), fall back
        // to the user's TC_Employees.HomeCampusId. Fixes Jennifer Tyler and
        // any supervisor whose Entra attributes have drifted — they'll still
        // see their own campus in scoped pages without needing Entra edits.
        // District users intentionally have a null CampusCode; the fallback
        // only fires for users with an actual employee record.
        if (string.IsNullOrEmpty(ctx.CampusCode))
        {
            await TryFallbackCampusFromDbAsync(ctx);
        }

        // Role flags from token role claims
        ctx.IsAdmin      = user.IsInRole(AppConstants.Roles.Admin);
        ctx.IsHR         = user.IsInRole(AppConstants.Roles.HR)         || ctx.IsAdmin;
        ctx.IsSupervisor = user.IsInRole(AppConstants.Roles.Supervisor)
                        || user.IsInRole(AppConstants.Roles.SupervisorStopSix)
                        || user.IsInRole(AppConstants.Roles.SupervisorMcCart)
                        || ctx.IsAdmin;
        ctx.IsHourly     = user.IsInRole(AppConstants.Roles.Employee)
                        || user.IsInRole(AppConstants.Roles.EmployeeStopSix)
                        || user.IsInRole(AppConstants.Roles.EmployeeMcCart)
                        || user.IsInRole(AppConstants.Roles.EmployeeStopSixPT)
                        || user.IsInRole(AppConstants.Roles.EmployeeMcCartPT);
        ctx.IsSubstitute  = user.IsInRole(AppConstants.Roles.Substitute);
        ctx.IsCampusAdmin = user.IsInRole(AppConstants.Roles.CampusAdmin) || ctx.IsSupervisor;
        ctx.IsAllStaff    = user.IsInRole(AppConstants.Roles.AllStaff)    || ctx.IsSupervisor || ctx.IsAdmin;
        ctx.IsReception = user.IsInRole(AppConstants.Roles.Reception) || ctx.IsCampusAdmin || ctx.IsAdmin;
        ctx.IsDistrict  = user.IsInRole(AppConstants.Roles.District) || ctx.IsHR || ctx.IsAdmin;

        return ctx;
    }

    /// <summary>
    /// Resolves the campus code from Entra properties.
    /// Department (NHSS/NHM/District) is the primary source.
    /// officeLocation ("New Heights - Stop Six" etc.) is the fallback.
    /// Returns null for District-scoped users who have no campus restriction.
    /// </summary>
    /// <summary>
    /// Last-resort campus resolution. When Entra department + officeLocation
    /// claims can't produce a CampusCode, try the user's TC_Employees row
    /// (EntraObjectId preferred; Email fallback). Sets ctx.CampusCode +
    /// OfficeLocation when we find a match. Silent no-op on any failure —
    /// never blocks page loads on a transient DB issue.
    /// </summary>
    private async Task TryFallbackCampusFromDbAsync(UserContext ctx)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();

            var query = db.TcEmployees
                .AsNoTracking()
                .Include(e => e.HomeCampus)
                .Where(e => e.IsActive);

            // Prefer EntraObjectId — stable across email renames.
            Data.Entities.TcEmployee? employee = null;
            if (!string.IsNullOrWhiteSpace(ctx.EntraObjectId))
            {
                employee = await query.FirstOrDefaultAsync(e => e.EntraObjectId == ctx.EntraObjectId);
            }
            if (employee == null && !string.IsNullOrWhiteSpace(ctx.Email))
            {
                var lower = ctx.Email.Trim().ToLower();
                employee = await query.FirstOrDefaultAsync(e => e.Email != null && e.Email.ToLower() == lower);
            }

            if (employee?.HomeCampus?.CampusCode is { } code && !string.IsNullOrWhiteSpace(code))
            {
                ctx.CampusCode = code;
                // If OfficeLocation wasn't set by claims, fill it from the
                // DB campus name so downstream display fields still work.
                if (string.IsNullOrWhiteSpace(ctx.OfficeLocation))
                {
                    ctx.OfficeLocation = employee.HomeCampus.CampusName;
                }
                _logger.LogInformation(
                    "UserContext campus resolved via DB fallback for {Email}: {Code}",
                    ctx.Email, code);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "UserContext DB campus fallback failed for {Email} — campus stays unresolved.",
                ctx.Email);
        }
    }

    private static string? ResolveCampusCode(string? department, string? officeLocation)
    {
        // Primary: department
        if (!string.IsNullOrWhiteSpace(department))
        {
            return department.Trim().ToUpperInvariant() switch
            {
                "NHSS" => AppConstants.Campus.StopSixCode,
                "NHM"  => AppConstants.Campus.McCartCode,
                "DISTRICT" => null,
                _ => null
            };
        }

        // Fallback: officeLocation
        if (!string.IsNullOrWhiteSpace(officeLocation))
        {
            var upper = officeLocation.Trim().ToUpperInvariant();
            if (upper.Contains("STOP SIX") || upper.Contains("STOPSIX") || upper.Contains("STOP6"))
                return AppConstants.Campus.StopSixCode;
            if (upper.Contains("MCCART") || upper.Contains("MC CART"))
                return AppConstants.Campus.McCartCode;
        }

        return null;
    }
}
