using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
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
    // Phase D5 / B1: SubstituteAdmin overlay diagnostics. Surfaced in the sync
    // UI so an admin can see whether the overlay ran, how many members were
    // returned from Graph, and how many SubRole flips occurred.
    public int OverlayPromoted { get; set; }
    public int OverlayDemoted { get; set; }
    public int? OverlayMembersReturned { get; set; }
    public bool OverlayConfigured { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public DateTime CompletedAt { get; set; } = DateTime.Now;
    public bool Success => !Errors.Any();
}

public class EmployeeSyncService : IEmployeeSyncService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly IGraphService _graph;
    private readonly IConfiguration _config;
    private readonly ILogger<EmployeeSyncService> _logger;
    private readonly IAuditService _audit;

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
        ( "GraphSync:EmployeeGroupIds:StopSix",           EmployeeType.HourlyStaff,     AppConstants.Campus.StopSixCode, false ),
        ( "GraphSync:EmployeeGroupIds:McCart",            EmployeeType.HourlyStaff,     AppConstants.Campus.McCartCode,  false ),
        ( "GraphSync:EmployeeGroupIds:StopSixPT",        EmployeeType.HourlyPartTime,  AppConstants.Campus.StopSixCode, false ),
        ( "GraphSync:EmployeeGroupIds:McCartPT",         EmployeeType.HourlyPartTime,  AppConstants.Campus.McCartCode,  false ),
        ( "GraphSync:EmployeeGroupIds:Substitute",        EmployeeType.Substitute,      null,                            true  ),
        // SubstituteAdmin members get a TcEmployees row (EmployeeType=Substitute)
        // from this pass so ApplyAdminSubOverlayAsync has a row to flip
        // SubRole=ADMIN_SUB on. Listed AFTER main Substitute so first-wins
        // keeps main-group membership primary when someone is in both groups
        // (order only matters for non-matching EmployeeTypes; harmless here
        // since both entries use EmployeeType.Substitute).
        ( "GraphSync:EmployeeGroupIds:SubstituteAdmin",   EmployeeType.Substitute,      null,                            true  ),
        ( "GraphSync:SupervisorGroupIds:StopSix",         EmployeeType.SalariedStaff,   AppConstants.Campus.StopSixCode, false ),
        ( "GraphSync:SupervisorGroupIds:McCart",          EmployeeType.SalariedStaff,   AppConstants.Campus.McCartCode,  false ),
        ( "GraphSync:TeacherGroupIds:StopSix",            EmployeeType.Teacher,         AppConstants.Campus.StopSixCode, false ),
        ( "GraphSync:TeacherGroupIds:McCart",             EmployeeType.Teacher,         AppConstants.Campus.McCartCode,  false ),
    };

    public EmployeeSyncService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IGraphService graph,
        IConfiguration config,
        ILogger<EmployeeSyncService> logger,
        IAuditService audit)
    {
        _dbFactory = dbFactory;
        _graph = graph;
        _config = config;
        _logger = logger;
        _audit = audit;
    }

    public async Task<EmployeeSyncResult> SyncFromEntraAsync(CancellationToken ct = default)
    {
        var result = new EmployeeSyncResult();
        _logger.LogInformation("Starting Entra employee sync");

        // Audit: batch start. EntityId is a constant marker since this is a batch-scope event
        // with no single entity target. AdminUi source because sync is triggered from
        // the EmployeeSync.razor admin page.
        await _audit.LogActionAsync(
            actionCode: AuditActions.EmployeeSync.SyncStarted,
            entityType: AuditEntityTypes.System,
            entityId: "ENTRA_SYNC",
            source: AuditSource.AdminUi,
            ct: ct);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Bulk teacher onboarding can insert 60+ TC_Employees rows plus matching
        // audit rows in a single SaveChanges. Default EF command timeout (30s) is
        // too tight on Azure SQL — bump to 5 minutes for this operation only.
        db.Database.SetCommandTimeout(300);

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
        // Also index by first-initial+lastname (e.g., "galvarez" for Griselda Alvarez)
        var staffByInitialLastName = allStaff
            .Where(s => !string.IsNullOrEmpty(s.FirstName) && !string.IsNullOrEmpty(s.LastName))
            .GroupBy(s => (s.FirstName![0] + s.LastName!).ToLower())
            .ToDictionary(g => g.Key, g => g.First());
        // Also index by FirstName + " " + LastName for Graph DisplayName matching
        var staffByFullName = allStaff
            .Where(s => !string.IsNullOrEmpty(s.FirstName) && !string.IsNullOrEmpty(s.LastName))
            .GroupBy(s => (s.FirstName! + " " + s.LastName!).ToLower())
            .ToDictionary(g => g.Key, g => g.First());

        var seenObjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (objectId, (user, empType, campusCode)) in userMap)
        {
            ct.ThrowIfCancellationRequested();
            seenObjectIds.Add(objectId);

            try
            {
                await SyncOneUserAsync(db, result, user, empType, campusCode, campuses,
                    existing, existingByEmail, staffByIdNumber, staffByUpnPrefix,
                    staffByInitialLastName, staffByFullName, ct);
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
            emp.ModifiedDate = DateTime.Now;
            result.Deactivated++;
            _logger.LogInformation("Deactivated employee {Id} - no longer in any group", emp.IdNumber);

            // Audit: per-user deactivation. EmployeeId is known (loaded from DB).
            await _audit.LogActionAsync(
                actionCode: AuditActions.EmployeeSync.Deactivated,
                entityType: AuditEntityTypes.Employee,
                entityId: emp.EmployeeId.ToString(),
                deltaSummary: $"Deactivated {emp.DisplayName ?? emp.IdNumber} — no longer in any Entra group",
                source: AuditSource.AdminUi,
                employeeId: emp.EmployeeId,
                campusId: emp.HomeCampusId,
                ct: ct);
        }

        if (toDeactivate.Any()) await db.SaveChangesAsync(ct);

        // Phase D5 / B1: admin-sub overlay. After the main sync completes, read
        // the SubstituteAdmin Entra group (if configured) and flip SubRole =
        // 'RECEPTION' on the corresponding TcEmployees rows. Subs in the main
        // Substitute group but NOT in this admin group are left as TEACHER
        // (or legacy NULL). Overlay fires after deactivation so we don't
        // thrash SubRole on rows we're about to deactivate.
        await ApplyAdminSubOverlayAsync(db, result, ct);

        result.CompletedAt = DateTime.Now;
        _logger.LogInformation("Sync complete: +{Added} updated:{Updated} deactivated:{Deact} skipped:{Skip} warnings:{Warn} errors:{Err}",
            result.Added, result.Updated, result.Deactivated, result.Skipped, result.Warnings.Count, result.Errors.Count);

        // Audit: batch end with full result summary in NewValues. Fires only on the
        // happy path — early-return failure paths still audit SYNC_STARTED but not
        // SYNC_COMPLETED, which is intentional so compliance queries can detect
        // "started but never completed" as a signal of failure.
        await _audit.LogActionAsync(
            actionCode: AuditActions.EmployeeSync.SyncCompleted,
            entityType: AuditEntityTypes.System,
            entityId: "ENTRA_SYNC",
            newValues: new
            {
                result.Added,
                result.Updated,
                result.Deactivated,
                result.Skipped,
                WarningCount = result.Warnings.Count,
                ErrorCount = result.Errors.Count,
                result.CompletedAt
            },
            deltaSummary: $"Added {result.Added}, Updated {result.Updated}, Deactivated {result.Deactivated}, Skipped {result.Skipped}, Warnings {result.Warnings.Count}, Errors {result.Errors.Count}",
            source: AuditSource.AdminUi,
            ct: ct);

        return result;
    }

    /// <summary>
    /// Phase D5 / B1: read the SubstituteAdmin Entra group (config key
    /// GraphSync:EmployeeGroupIds:SubstituteAdmin) and flip SubRole='ADMIN_SUB'
    /// on TcEmployees rows for any member. Employees NOT in this admin group
    /// whose SubRole is currently 'ADMIN_SUB' get reset to NULL (interpreted
    /// by the teacher-pool query as legacy-TEACHER_SUB for Substitute rows,
    /// or "not a sub" for non-Substitute rows).
    ///
    /// Works across all EmployeeTypes — a HourlyPartTime receptionist in the
    /// admin Entra group gets SubRole='ADMIN_SUB' without losing her primary
    /// HourlyPartTime classification.
    ///
    /// Uses transitive membership (admin sub group may be nested). Errors are
    /// logged + added to result.Warnings; main sync result is not failed by
    /// overlay errors.
    /// </summary>
    private async Task ApplyAdminSubOverlayAsync(
        TimeClockDbContext db,
        EmployeeSyncResult result,
        CancellationToken ct)
    {
        const string configKey = "GraphSync:EmployeeGroupIds:SubstituteAdmin";
        var adminGroupId = _config[configKey];
        if (string.IsNullOrWhiteSpace(adminGroupId))
        {
            result.OverlayConfigured = false;
            _logger.LogInformation("SubstituteAdmin group not configured ({Key}) — skipping admin-sub overlay", configKey);
            return;
        }

        result.OverlayConfigured = true;

        try
        {
            var members = await _graph.GetTransitiveMembersAsync(adminGroupId, ct);
            var adminObjectIds = members
                .Select(m => m.ObjectId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            result.OverlayMembersReturned = adminObjectIds.Count;
            _logger.LogInformation("SubstituteAdmin overlay: {Count} group members", adminObjectIds.Count);

            if (adminObjectIds.Count == 0)
            {
                result.Warnings.Add($"SubstituteAdmin overlay: group {adminGroupId} returned 0 transitive members. Verify group ID, transitive membership, and that User.Read.All has admin consent.");
            }

            // All active employees regardless of EmployeeType. SubRole='ADMIN_SUB'
            // is a cross-cutting role tag — a HourlyPartTime receptionist who is
            // also in the SubstituteAdmin Entra group should get SubRole flipped
            // to ADMIN_SUB without losing her primary HourlyPartTime
            // classification. Filtering this to EmployeeType=Substitute would
            // silently skip her. Filter is done in memory after load because EF
            // translation of HashSet<string>.Contains against a client-side set
            // is inconsistent across providers.
            //
            // Demotion caveat: this overlay is authoritative on SubRole. If
            // someone has SubRole='ADMIN_SUB' set manually but is not in the
            // admin group, next sync will demote them. Entra group is source
            // of truth; manual flips via SubPoolManagement are intended as
            // short-lived overrides until the next sync.
            var allActive = await db.TcEmployees
                .Where(e => e.IsActive)
                .ToListAsync(ct);

            // Only employees either currently tagged ADMIN_SUB or in the admin
            // group are candidates for a SubRole change. The rest are no-ops
            // (NULL → NULL) and don't need to be touched.
            var candidates = allActive
                .Where(e => string.Equals(e.SubRole, "ADMIN_SUB", StringComparison.OrdinalIgnoreCase)
                         || (!string.IsNullOrWhiteSpace(e.EntraObjectId)
                             && adminObjectIds.Contains(e.EntraObjectId)))
                .ToList();

            int candidatesWithoutObjectId = candidates.Count(s => string.IsNullOrWhiteSpace(s.EntraObjectId));
            if (candidatesWithoutObjectId > 0)
            {
                result.Warnings.Add($"SubstituteAdmin overlay: {candidatesWithoutObjectId} admin-pool employee(s) have no EntraObjectId and cannot be matched against the admin group. Run main sync first so new employees get linked.");
            }

            int promoted = 0, demoted = 0;
            foreach (var sub in candidates)
            {
                bool isAdmin = !string.IsNullOrWhiteSpace(sub.EntraObjectId)
                            && adminObjectIds.Contains(sub.EntraObjectId);
                // "Promote" means SubRole becomes ADMIN_SUB. "Demote" means
                // SubRole is cleared — NULL for non-Substitute employees
                // (they have no sub role at all) and NULL is interpreted as
                // legacy-TEACHER_SUB by the teacher-pool query for Substitute
                // rows. We could explicitly write TEACHER_SUB on demote to be
                // less ambiguous but NULL matches existing data shape and
                // keeps the overlay idempotent on non-sub employees.
                string? desired = isAdmin ? "ADMIN_SUB" : null;
                if (!string.Equals(sub.SubRole, desired, StringComparison.OrdinalIgnoreCase))
                {
                    sub.SubRole = desired;
                    sub.ModifiedDate = DateTime.Now;
                    if (isAdmin) promoted++; else demoted++;
                }
            }

            result.OverlayPromoted = promoted;
            result.OverlayDemoted = demoted;

            if (promoted + demoted > 0)
            {
                await db.SaveChangesAsync(ct);
                _logger.LogInformation("SubstituteAdmin overlay: promoted {Promoted} / demoted {Demoted}",
                    promoted, demoted);
            }
            else
            {
                _logger.LogInformation("SubstituteAdmin overlay: no SubRole changes needed");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SubstituteAdmin overlay failed — main sync result unaffected");
            result.Warnings.Add($"SubstituteAdmin overlay failed: {ex.Message}");
        }
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
        Dictionary<string, Staff> staffByInitialLastName,
        Dictionary<string, Staff> staffByFullName,
        CancellationToken ct)
    {
        Staff? staff = null;

        // Strategy 1: Graph DisplayName match (e.g., "Griselda Alvarez")
        var displayNameKey = (user.DisplayName ?? "").ToLower().Trim();
        if (!string.IsNullOrEmpty(displayNameKey) && staffByFullName.TryGetValue(displayNameKey, out var byName))
            staff = byName;

        // Strategy 2: UPN prefix as firstname.lastname (e.g., "griselda.alvarez")
        if (staff == null)
        {
            var upn = (user.Email ?? "").ToLower();
            var atIdx = upn.IndexOf('@');
            var upnPrefix = atIdx > 0 ? upn[..atIdx] : upn;
            if (!string.IsNullOrEmpty(upnPrefix) && staffByUpnPrefix.TryGetValue(upnPrefix, out var byUpn))
                staff = byUpn;
        }

        // Strategy 3: UPN prefix as first-initial+lastname (e.g., "galvarez")
        if (staff == null)
        {
            var upn = (user.Email ?? "").ToLower();
            var atIdx = upn.IndexOf('@');
            var upnPrefix = atIdx > 0 ? upn[..atIdx] : upn;
            if (!string.IsNullOrEmpty(upnPrefix) && staffByInitialLastName.TryGetValue(upnPrefix, out var byInit))
                staff = byInit;
        }

        // Strategy 4 (REMOVED 2026-04-20): EmployeeId -> IdNumber matching was
        // wrong-mapping teachers to unrelated Staff rows because Entra's
        // employeeId attribute is the Ascender/CSS payroll number, while
        // Staff.IdNumber is the PowerSchool local ID — different ID systems
        // that happen to be small integers. Example collision: Amber Robins
        // (Entra EmployeeId 000111) was being matched to Leighla Diaz's Staff
        // row (PowerSchool IdNumber 111). Teachers that don't match Strategies
        // 1-3 are now correctly Skipped with a warning so they can be triaged
        // individually rather than incorrectly inserted under another person's
        // IdNumber.

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
                StaffDcid          = staff?.Dcid,
                DisplayName        = user.DisplayName,
                IdNumber           = staff?.IdNumber ?? await GetNextSubIdAsync(db, ct),
                AscenderEmployeeId = PadAscenderId(user.EmployeeId),
                Email              = user.Email,
                EmployeeType       = empType,
                Shift              = mappedShift,
                HomeCampusId       = campusId,
                EntraObjectId      = user.ObjectId,
                DepartmentCode     = user.Department,
                IsActive           = true,
                CreatedDate        = DateTime.Now,
                ModifiedDate       = DateTime.Now,
            };
            db.TcEmployees.Add(emp);
            existingByObjectId[user.ObjectId] = emp;
            if (!string.IsNullOrEmpty(user.Email)) existingByEmail[user.Email.ToLower()] = emp;
            result.Added++;
            _logger.LogInformation("Added employee {Name} ({Email}) as {Type} shift={Shift}", user.DisplayName, user.Email, empType, emp.Shift);

            // Audit: per-user creation. EmployeeId is not yet assigned (bulk SaveChanges
            // runs later), so EntityId uses EntraObjectId, which IS stable. Post-save
            // queries can join EntraObjectId -> TC_Employees.EmployeeId when needed.
            var staffMatchNote = staff != null ? $" [staff-matched to {staff.FirstName} {staff.LastName}]" : "";
            await _audit.LogActionAsync(
                actionCode: AuditActions.EmployeeSync.Created,
                entityType: AuditEntityTypes.Employee,
                entityId: user.ObjectId,
                newValues: new
                {
                    emp.EntraObjectId,
                    emp.DisplayName,
                    emp.Email,
                    emp.IdNumber,
                    emp.AscenderEmployeeId,
                    EmployeeType = emp.EmployeeType.ToString(),
                    Shift = emp.Shift.ToString(),
                    emp.HomeCampusId,
                    emp.StaffDcid
                },
                deltaSummary: $"Added {user.DisplayName} ({user.Email}) as {empType} shift={emp.Shift}{staffMatchNote}",
                source: AuditSource.AdminUi,
                campusId: emp.HomeCampusId,
                ct: ct);
        }
        else
        {
            bool changed = false;
            var changedFields = new List<string>();
            var wasInactive = !emp.IsActive;
            var paddedAscenderId = PadAscenderId(user.EmployeeId);
            if (emp.EntraObjectId != user.ObjectId)    { emp.EntraObjectId  = user.ObjectId;  changed = true; changedFields.Add("EntraObjectId"); }
            if (emp.DisplayName != user.DisplayName)   { emp.DisplayName    = user.DisplayName; changed = true; changedFields.Add("DisplayName"); }
            if (emp.Email != user.Email)               { emp.Email          = user.Email;      changed = true; changedFields.Add("Email"); }
            if (emp.EmployeeType != empType)           { emp.EmployeeType   = empType;         changed = true; changedFields.Add("EmployeeType"); }
            if (emp.Shift != mappedShift)              { emp.Shift          = mappedShift;     changed = true; changedFields.Add("Shift"); }
            if (emp.HomeCampusId != campusId)          { emp.HomeCampusId   = campusId;        changed = true; changedFields.Add("HomeCampusId"); }
            if (emp.DepartmentCode != user.Department) { emp.DepartmentCode = user.Department; changed = true; changedFields.Add("DepartmentCode"); }
            if (staff != null && emp.StaffDcid != staff.Dcid) { emp.StaffDcid = staff.Dcid;   changed = true; changedFields.Add("StaffDcid"); }
            if (emp.AscenderEmployeeId != paddedAscenderId && paddedAscenderId != null)
                                                       { emp.AscenderEmployeeId = paddedAscenderId; changed = true; changedFields.Add("AscenderEmployeeId"); }
            if (!emp.IsActive)                         { emp.IsActive       = true;            changed = true; changedFields.Add("IsActive"); }

            if (changed)
            {
                emp.ModifiedDate = DateTime.Now;
                result.Updated++;

                // Audit: per-user update. If the only reason changed=true is that we flipped
                // IsActive back on, surface that as a REACTIVATED action instead of UPDATED
                // so compliance reports can find "who came back" quickly.
                var isReactivationOnly = wasInactive && changedFields.Count == 1 && changedFields[0] == "IsActive";
                await _audit.LogActionAsync(
                    actionCode: isReactivationOnly ? AuditActions.EmployeeSync.Reactivated : AuditActions.EmployeeSync.Updated,
                    entityType: AuditEntityTypes.Employee,
                    entityId: emp.EmployeeId.ToString(),
                    newValues: new
                    {
                        emp.DisplayName,
                        emp.Email,
                        emp.IdNumber,
                        emp.AscenderEmployeeId,
                        EmployeeType = emp.EmployeeType.ToString(),
                        Shift = emp.Shift.ToString(),
                        emp.HomeCampusId,
                        emp.StaffDcid,
                        emp.IsActive
                    },
                    deltaSummary: isReactivationOnly
                        ? $"Reactivated {emp.DisplayName ?? emp.IdNumber}"
                        : $"Updated {emp.DisplayName ?? emp.IdNumber}: {string.Join(", ", changedFields)}",
                    source: AuditSource.AdminUi,
                    employeeId: emp.EmployeeId,
                    campusId: emp.HomeCampusId,
                    ct: ct);
            }
            else result.Skipped++;
        }
    }

    /// <summary>
    /// Formats the Entra EmployeeId attribute as a 6-digit zero-padded string
    /// to match the CSS/Ascender "Emp Nbr" column (e.g. "76" -> "000076").
    /// Returns null when Entra has no value — callers must treat null as
    /// "leave existing AscenderEmployeeId alone" so we never wipe a good
    /// value for a sub whose Entra attribute is temporarily missing.
    /// Values already 6+ characters are returned unchanged (no truncation).
    /// </summary>
    private static string? PadAscenderId(string? entraEmployeeId)
    {
        if (string.IsNullOrWhiteSpace(entraEmployeeId)) return null;
        return entraEmployeeId.Trim().PadLeft(6, '0');
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
                        emp.ModifiedDate = DateTime.Now;
                    }
                }
                else if (emp.SupervisorEmployeeId != null)
                {
                    emp.SupervisorEmployeeId = null;
                    emp.ModifiedDate = DateTime.Now;
                }
                continue;
            }

            if (empByObjectId.TryGetValue(manager.ObjectId, out var managerEmp))
            {
                if (emp.SupervisorEmployeeId != managerEmp.EmployeeId)
                {
                    emp.SupervisorEmployeeId = managerEmp.EmployeeId;
                    emp.ModifiedDate = DateTime.Now;
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
                ModifiedDate = DateTime.Now
            };
            db.TcSystemConfigs.Add(config);
        }
        else
        {
            next = int.TryParse(config.ConfigValue, out var n) ? n + 1 : 1;
            config.ConfigValue = next.ToString();
            config.ModifiedDate = DateTime.Now;
        }
        return $"SUB-{next:D4}";
    }
}
