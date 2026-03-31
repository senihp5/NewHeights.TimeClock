using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Constants;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

public interface IEmployeeSyncService
{
    Task<EmployeeSyncResult> SyncFromEntraAsync(CancellationToken ct = default);
}

public class EmployeeSyncResult
{
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Deactivated { get; set; }
    public int Skipped { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
    public bool Success => !Errors.Any();
}

public class EmployeeSyncService : IEmployeeSyncService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly IGraphService _graph;
    private readonly IConfiguration _config;
    private readonly ILogger<EmployeeSyncService> _logger;

    // Config key -> (EmployeeType, campus code)
    // Priority order: first entry wins when a user appears in multiple groups.
    // Hourly/Substitute take priority over Salaried so a supervisor who is also
    // in the hourly group keeps their HourlyStaff type.
    // Key format must match Azure env var names (double-underscore -> colon):
    // GraphSync__EmployeeGroupIds__StopSix    -> GraphSync:EmployeeGroupIds:StopSix
    // GraphSync__SupervisorGroupIds__StopSix  -> GraphSync:SupervisorGroupIds:StopSix
    // useTransitive=true expands nested group membership (needed for Substitute group)
    private static readonly (string ConfigKey, EmployeeType Type, string? Campus, bool UseTransitive)[] GroupMeta =
    {
        ( "GraphSync:EmployeeGroupIds:StopSix",           EmployeeType.HourlyStaff,   AppConstants.Campus.StopSixCode, false ),
        ( "GraphSync:EmployeeGroupIds:McCart",            EmployeeType.HourlyStaff,   AppConstants.Campus.McCartCode,  false ),
        ( "GraphSync:EmployeeGroupIds:Substitute",        EmployeeType.Substitute,    null,                            true  ),
        ( "GraphSync:SupervisorGroupIds:StopSix",         EmployeeType.SalariedStaff, AppConstants.Campus.StopSixCode, false ),
        ( "GraphSync:SupervisorGroupIds:McCart",          EmployeeType.SalariedStaff, AppConstants.Campus.McCartCode,  false ),
    };

    public EmployeeSyncService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IGraphService graph,
        IConfiguration config,
        ILogger<EmployeeSyncService> logger)
    {
        _dbFactory = dbFactory;
        _graph = graph;
        _config = config;
        _logger = logger;
    }

    public async Task<EmployeeSyncResult> SyncFromEntraAsync(CancellationToken ct = default)
    {
        var result = new EmployeeSyncResult();
        _logger.LogInformation("Starting Entra employee sync");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var campuses = await db.Campuses.ToDictionaryAsync(c => c.CampusCode, c => c.CampusId, ct);

        // Collect members from all groups; highest-priority group wins per user (StopSix > McCart > Substitute)
        var userMap = new Dictionary<string, (GraphUserInfo User, EmployeeType Type, string? CampusCode)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (configKey, empType, campusCode, useTransitive) in GroupMeta)
        {
            var groupId = _config[configKey];
            if (string.IsNullOrWhiteSpace(groupId))
            {
                result.Warnings.Add($"Group ID not configured for {configKey} - skipping");
                continue;
            }

            _logger.LogInformation("Fetching group {Key} ({GroupId}) transitive={Trans}", configKey, groupId, useTransitive);
            var members = useTransitive
                ? await _graph.GetTransitiveMembersAsync(groupId, ct)
                : await _graph.GetGroupMembersAsync(groupId, ct);
            _logger.LogInformation("Group {Key}: {Count} members returned", configKey, members.Count);

            if (members.Count == 0)
                result.Warnings.Add($"Group {configKey} returned 0 members - verify group ID and User.Read.All permission");

            foreach (var m in members)
            {
                if (!userMap.ContainsKey(m.ObjectId))
                    userMap[m.ObjectId] = (m, empType, campusCode);
            }
        }

        if (!userMap.Any())
        {
            result.Errors.Add("No users returned from any configured group. Verify User.Read.All (Application) permission is granted in Azure with admin consent, then retry.");
            return result;
        }

        var existing = await db.TcEmployees.ToDictionaryAsync(e => e.EntraObjectId ?? "", e => e, ct);
        var existingByEmail = await db.TcEmployees
            .Where(e => e.Email != null)
            .ToDictionaryAsync(e => e.Email!.ToLower(), e => e, ct);

        var allStaff = await db.Staff.ToListAsync(ct);
        var staffByIdNumber = allStaff
            .Where(s => s.IdNumber != null)
            .GroupBy(s => s.IdNumber!.TrimStart('0').ToLower())
            .ToDictionary(g => g.Key, g => g.First());
        var staffByUpnPrefix = allStaff
            .Where(s => s.FirstName != null && s.LastName != null)
            .GroupBy(s => (s.FirstName! + "." + s.LastName!).ToLower())
            .ToDictionary(g => g.Key, g => g.First());

        var seenObjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (objectId, (user, empType, campusCode)) in userMap)
        {
            ct.ThrowIfCancellationRequested();
            seenObjectIds.Add(objectId);

            try
            {
                await SyncOneUserAsync(db, result, user, empType, campusCode, campuses,
                    existing, existingByEmail, staffByIdNumber, staffByUpnPrefix, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error syncing user {ObjectId} ({Name})", objectId, user.DisplayName);
                result.Errors.Add($"Error syncing {user.DisplayName}: {ex.Message}");
            }
        }

        try { await db.SaveChangesAsync(ct); }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
        {
            var inner = dbEx.InnerException?.Message ?? dbEx.Message;
            _logger.LogError(dbEx, "SaveChanges failed: {Inner}", inner);
            result.Errors.Add($"Database save failed: {inner}");
            return result;
        }

        await LinkSupervisorsAsync(db, result, userMap, ct);
        await db.SaveChangesAsync(ct);

        var toDeactivate = await db.TcEmployees
            .Where(e => e.IsActive && e.EntraObjectId != null && !seenObjectIds.Contains(e.EntraObjectId))
            .ToListAsync(ct);

        foreach (var emp in toDeactivate)
        {
            emp.IsActive = false;
            emp.ModifiedDate = DateTime.UtcNow;
            result.Deactivated++;
            _logger.LogInformation("Deactivated employee {Id} - no longer in any group", emp.IdNumber);
        }

        if (toDeactivate.Any()) await db.SaveChangesAsync(ct);

        result.CompletedAt = DateTime.UtcNow;
        _logger.LogInformation("Sync complete: +{Added} updated:{Updated} deactivated:{Deact} skipped:{Skip} warnings:{Warn} errors:{Err}",
            result.Added, result.Updated, result.Deactivated, result.Skipped, result.Warnings.Count, result.Errors.Count);

        return result;
    }

    private async Task SyncOneUserAsync(
        TimeClockDbContext db,
        EmployeeSyncResult result,
        GraphUserInfo user,
        EmployeeType empType,
        string? campusCode,
        Dictionary<string, int> campuses,
        Dictionary<string, TcEmployee> existingByObjectId,
        Dictionary<string, TcEmployee> existingByEmail,
        Dictionary<string, Staff> staffByIdNumber,
        Dictionary<string, Staff> staffByUpnPrefix,
        CancellationToken ct)
    {
        Staff? staff = null;
        var empIdKey = (user.EmployeeId ?? "").TrimStart('0').ToLower();
        if (!string.IsNullOrEmpty(empIdKey) && staffByIdNumber.TryGetValue(empIdKey, out var byId))
            staff = byId;

        if (staff == null)
        {
            var upn = (user.Email ?? "").ToLower();
            var atIdx = upn.IndexOf('@');
            var upnPrefix = atIdx > 0 ? upn[..atIdx] : upn;
            if (!string.IsNullOrEmpty(upnPrefix) && staffByUpnPrefix.TryGetValue(upnPrefix, out var byUpn))
                staff = byUpn;
        }

        if (staff == null && empType != EmployeeType.Substitute)
        {
            result.Warnings.Add($"No Staff record found for {user.DisplayName} ({user.Email}) - skipping");
            result.Skipped++;
            return;
        }
        // Substitutes may not exist in PowerSchool - proceed with Entra data only

        int campusId;
        if (campusCode != null && campuses.TryGetValue(campusCode, out var gid))
        {
            campusId = gid;
        }
        else
        {
            var schoolName = staff?.SchoolName ?? "";
            if (schoolName.Contains("McCart", StringComparison.OrdinalIgnoreCase) && campuses.TryGetValue(AppConstants.Campus.McCartCode, out var mc))
                campusId = mc;
            else if (campuses.TryGetValue(AppConstants.Campus.StopSixCode, out var ss))
                campusId = ss;
            else
                campusId = campuses.Values.First();
        }

        TcEmployee? emp = null;
        if (!string.IsNullOrEmpty(user.ObjectId) && existingByObjectId.TryGetValue(user.ObjectId, out var byOid))
            emp = byOid;
        else if (!string.IsNullOrEmpty(user.Email) && existingByEmail.TryGetValue(user.Email.ToLower(), out var byEmail))
            emp = byEmail;

        var mappedShift = MapJobTitleToShift(user.JobTitle, empType);

        if (emp == null)
        {
            emp = new TcEmployee
            {
                StaffDcid      = staff?.Dcid,
                IdNumber       = staff?.IdNumber ?? await GetNextSubIdAsync(db, ct),
                Email          = user.Email,
                EmployeeType   = empType,
                Shift          = mappedShift,
                HomeCampusId   = campusId,
                EntraObjectId  = user.ObjectId,
                DepartmentCode = user.Department,
                IsActive       = true,
                CreatedDate    = DateTime.UtcNow,
                ModifiedDate   = DateTime.UtcNow,
            };
            db.TcEmployees.Add(emp);
            existingByObjectId[user.ObjectId] = emp;
            if (!string.IsNullOrEmpty(user.Email)) existingByEmail[user.Email.ToLower()] = emp;
            result.Added++;
            _logger.LogInformation("Added employee {Name} ({Email}) as {Type} shift={Shift}", user.DisplayName, user.Email, empType, emp.Shift);
        }
        else
        {
            bool changed = false;
            if (emp.EntraObjectId != user.ObjectId)    { emp.EntraObjectId  = user.ObjectId;  changed = true; }
            if (emp.Email != user.Email)               { emp.Email          = user.Email;      changed = true; }
            if (emp.EmployeeType != empType)           { emp.EmployeeType   = empType;         changed = true; }
            if (emp.Shift != mappedShift)              { emp.Shift          = mappedShift;     changed = true; }
            if (emp.HomeCampusId != campusId)          { emp.HomeCampusId   = campusId;        changed = true; }
            if (emp.DepartmentCode != user.Department) { emp.DepartmentCode = user.Department; changed = true; }
            if (staff != null && emp.StaffDcid != staff.Dcid) { emp.StaffDcid = staff.Dcid;   changed = true; }
            if (!emp.IsActive)                         { emp.IsActive       = true;            changed = true; }

            if (changed) { emp.ModifiedDate = DateTime.UtcNow; result.Updated++; }
            else result.Skipped++;
        }
    }

    /// <summary>
    /// Maps the HR job title stored in Entra (synced from PowerSchool) to an
    /// <see cref="EmployeeShift"/> value.
    ///
    /// Rules (evaluated top-to-bottom; first match wins):
    ///   "EVENING * TEACHER"  (contains EVENING and TEACHER)  -> Evening
    ///   "* TEACHER PT"       (ends with TEACHER PT)          -> Day
    ///   "* TEACHER"          (contains TEACHER)              -> Day
    ///   "SUBSTITUTE- DAYTIME" only                           -> Day
    ///   "SUBSTITUTE- EVENING" only                           -> Evening
    ///   Both substitute titles present on same person        -> Both  (handled by empType + null title)
    ///   Everything else                                       -> Day
    ///
    /// Substitutes with a null/empty job title default to Both because they
    /// may be dispatched to either session.
    /// </summary>
    private static EmployeeShift MapJobTitleToShift(string? jobTitle, EmployeeType empType)
    {
        if (empType == EmployeeType.Substitute)
        {
            if (string.IsNullOrWhiteSpace(jobTitle))
                return EmployeeShift.Both;

            var titleUp = jobTitle.ToUpperInvariant().Trim();

            bool isDaytime = titleUp.Contains("DAYTIME");
            bool isEvening = titleUp.Contains("EVENING");

            if (isDaytime && isEvening) return EmployeeShift.Both;
            if (isEvening)             return EmployeeShift.Evening;
            if (isDaytime)             return EmployeeShift.Day;

            // Unrecognized substitute title — default to Both (covers both sessions)
            return EmployeeShift.Both;
        }

        // Non-substitute: parse the teacher/staff job title
        if (string.IsNullOrWhiteSpace(jobTitle))
            return EmployeeShift.Day;

        var t = jobTitle.ToUpperInvariant().Trim();

        // Evening teacher: title contains both EVENING and TEACHER
        if (t.Contains("EVENING") && t.Contains("TEACHER"))
            return EmployeeShift.Evening;

        // Part-time teacher suffix (daytime)
        if (t.EndsWith("TEACHER PT") || t.EndsWith("TEACHER- PT") || t.EndsWith("TEACHER PT."))
            return EmployeeShift.Day;

        // Any other teacher title -> Day
        if (t.Contains("TEACHER"))
            return EmployeeShift.Day;

        // Non-teaching staff -> Day
        return EmployeeShift.Day;
    }

    private async Task LinkSupervisorsAsync(
        TimeClockDbContext db,
        EmployeeSyncResult result,
        Dictionary<string, (GraphUserInfo User, EmployeeType Type, string? CampusCode)> userMap,
        CancellationToken ct)
    {
        var empByObjectId = await db.TcEmployees
            .Where(e => e.EntraObjectId != null)
            .ToDictionaryAsync(e => e.EntraObjectId!, e => e, ct);

        // Build a fallback supervisor map: campus -> lead SalariedStaff employee
        // Used when a substitute has no Entra manager assigned
        var fallbackSupervisors = await db.TcEmployees
            .Where(e => e.IsActive && e.EmployeeType == EmployeeType.SalariedStaff)
            .GroupBy(e => e.HomeCampusId)
            .ToDictionaryAsync(g => g.Key, g => g.First(), ct);

        foreach (var (objectId, (user, empType, _)) in userMap)
        {
            if (!empByObjectId.TryGetValue(objectId, out var emp)) continue;

            var manager = await _graph.GetUserManagerAsync(objectId, ct);
            if (manager == null)
            {
                if (empType == EmployeeType.Substitute)
                {
                    // Substitutes have no Entra manager - assign campus supervisor as fallback
                    if (fallbackSupervisors.TryGetValue(emp.HomeCampusId, out var fallback) &&
                        emp.SupervisorEmployeeId != fallback.EmployeeId)
                    {
                        emp.SupervisorEmployeeId = fallback.EmployeeId;
                        emp.ModifiedDate = DateTime.UtcNow;
                    }
                }
                else if (emp.SupervisorEmployeeId != null)
                {
                    emp.SupervisorEmployeeId = null;
                    emp.ModifiedDate = DateTime.UtcNow;
                }
                continue;
            }

            if (empByObjectId.TryGetValue(manager.ObjectId, out var managerEmp))
            {
                if (emp.SupervisorEmployeeId != managerEmp.EmployeeId)
                {
                    emp.SupervisorEmployeeId = managerEmp.EmployeeId;
                    emp.ModifiedDate = DateTime.UtcNow;
                }
            }
            else
            {
                result.Warnings.Add($"{user.DisplayName}s manager ({manager.DisplayName}) is not in TC_Employees - supervisor not linked");
            }
        }
    }

    private static async Task<string> GetNextSubIdAsync(TimeClockDbContext db, CancellationToken ct)
    {
        const string configKey = "Substitute.LastIdNumber";
        var config = await db.TcSystemConfigs.FindAsync(new object[] { configKey }, ct);
        int next;
        if (config == null)
        {
            // Find the highest existing SUB- number to avoid gaps on re-seed
            var existingMax = await db.TcEmployees
                .Where(e => e.IdNumber != null && e.IdNumber.StartsWith("SUB-"))
                .Select(e => e.IdNumber!)
                .ToListAsync(ct);
            next = existingMax
                .Select(id => int.TryParse(id.Substring(4), out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;
            config = new NewHeights.TimeClock.Data.Entities.TcSystemConfig
            {
                ConfigKey    = configKey,
                ConfigValue  = next.ToString(),
                ConfigType   = "INT",
                Description  = "Auto-increment counter for substitute employee IDs",
                ModifiedBy   = "EmployeeSync",
                ModifiedDate = DateTime.UtcNow
            };
            db.TcSystemConfigs.Add(config);
        }
        else
        {
            next = int.TryParse(config.ConfigValue, out var n) ? n + 1 : 1;
            config.ConfigValue = next.ToString();
            config.ModifiedDate = DateTime.UtcNow;
        }
        return $"SUB-{next:D4}";
    }
}
