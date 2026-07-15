using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Reads section + enrollment data from CMS (CaseManagementDB) and
/// lazy-materializes it into local TC_ClassSection + TC_SectionEnrollment
/// tables.
///
/// IMPORTANT - cross-DB pattern:
///   Azure SQL Database (single database tier) does NOT support
///   three-part name cross-database queries (Msg 40515). Even though
///   IDCardPrinterDB and CaseManagementDB live on the same SQL server
///   with the same login, queries like SELECT ... FROM
///   CaseManagementDB.dbo.&lt;table&gt; will fail when issued from a
///   connection opened to IDCardPrinterDB.
///
///   This service opens a SEPARATE connection to CaseManagementDB
///   (same server + credentials, different Initial Catalog) for every
///   CMS read. Local TC table writes still use the EF DbContext
///   against IDCardPrinterDB.
///
///   The pre-existing MasterScheduleLookupService uses the (failing)
///   cross-DB pattern and silently falls back to local-only data.
///   That service should be migrated to this dedicated-connection
///   pattern as well; tracked as a follow-up.
///
/// Materialization rules:
///   - GetSectionsForTeacherAsync bulk-syncs the teacher's current-term
///     sections from CMS before reading local cache.
///   - GetSectionAsync(psSectionId) reads local first; if missing,
///     materializes a single section.
///   - GetRosterAsync ensures enrollments are materialized for the
///     section if they're stale (older than EnrollmentRefreshAfter).
///
/// CMS SYNC UPDATE (2026-05-28):
///   Advising_StudentSchedule NOW carries SectionID directly + the
///   denormalized StudentNumber + StudentFirstName + StudentLastName
///   columns. The natural-key join (TermId + CourseNumber + ...)
///   and the LEFT JOIN to Core_Students that were originally needed
///   have been removed - materialization is now a single-column lookup.
///
///   See [[reference-cms-sync-class-attendance]] memory for the full
///   handoff doc + the new PowerSchool SchoolIDs 888 (Summer Term)
///   and 777 (Charter School Waitlist) which require the virtual
///   campus seed rows from migration 068.
/// </summary>
public class ClassRosterService : IClassRosterService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ClassRosterService> _logger;

    /// <summary>
    /// How stale an enrollment row can be before re-syncing on read.
    /// CMS background sync runs hourly per the 2026-05-28 handoff doc,
    /// so 2h gives us at-most-one-sync staleness without thrashing.
    /// </summary>
    private static readonly TimeSpan EnrollmentRefreshAfter = TimeSpan.FromHours(2);

    /// <summary>
    /// How stale a section row can be before re-syncing on read.
    /// Sections change rarely (term registry / teacher assignments),
    /// so 4h is fine even though CMS runs hourly.
    /// </summary>
    private static readonly TimeSpan SectionRefreshAfter = TimeSpan.FromHours(4);

    public ClassRosterService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        IConfiguration configuration,
        ILogger<ClassRosterService> logger)
    {
        _dbFactory = dbFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Opens a connection directly to CaseManagementDB. Reuses the
    /// DefaultConnection string but swaps Initial Catalog so we land
    /// on the CMS database. Caller is responsible for disposing.
    /// </summary>
    private async Task<SqlConnection> OpenCmsConnectionAsync(CancellationToken ct)
    {
        var defaultConn = _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "DefaultConnection not configured - cannot open CMS connection");
        var builder = new SqlConnectionStringBuilder(defaultConn)
        {
            InitialCatalog = "CaseManagementDB"
        };
        var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────

    public async Task<int?> ResolveTeacherDcidFromEmailAsync(
        string email, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var trimmed = email.Trim().ToLowerInvariant();

        using var cmsConn = await OpenCmsConnectionAsync(ct);

        using var cmd = cmsConn.CreateCommand();
        cmd.CommandText = @"
            SELECT TOP 1 StaffDCID
            FROM dbo.Core_Staff
            WHERE LOWER(Email) = @email
              AND IsActive = 1
            ORDER BY StaffID";

        var p = cmd.CreateParameter();
        p.ParameterName = "@email";
        p.DbType = DbType.String;
        p.Value = trimmed;
        cmd.Parameters.Add(p);

        var raw = await cmd.ExecuteScalarAsync(ct);
        if (raw == null || raw == DBNull.Value)
        {
            _logger.LogInformation(
                "ResolveTeacherDcidFromEmailAsync: no Core_Staff row matched {Email}", trimmed);
            return null;
        }

        var dcid = Convert.ToInt64(raw);
        _logger.LogInformation(
            "ResolveTeacherDcidFromEmailAsync: matched {Email} -> StaffDCID {Dcid}",
            trimmed, dcid);
        return (int)dcid;
    }

    public async Task<int> EnsureSectionCachedAsync(
        long psSectionId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await EnsureSectionCachedInternalAsync(db, psSectionId, ct);
    }

    public async Task<TcClassSection?> GetSectionAsync(
        long psSectionId, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);

        var local = await db.TcClassSections
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.PsSectionId == psSectionId, ct);

        if (local != null && (DateTime.Now - local.LastSyncDate) < SectionRefreshAfter)
            return local;

        var classSectionId = await EnsureSectionCachedInternalAsync(db, psSectionId, ct);
        return await db.TcClassSections
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ClassSectionId == classSectionId, ct);
    }

    public async Task<List<ClassSectionSummary>> GetSectionsForTeacherAsync(
        int teacherDcid, DateOnly date, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);

        var staff = await db.Staff
            .AsNoTracking()
            .Where(s => s.Dcid == teacherDcid && s.IsActive)
            .Select(s => new { s.Id, s.SchoolId })
            .FirstOrDefaultAsync(ct);

        if (staff?.Id == null)
        {
            _logger.LogWarning(
                "GetSectionsForTeacherAsync: Staff.Dcid {Dcid} has no PS Id or is inactive",
                teacherDcid);
            return new();
        }

        await SyncTeacherSectionsForDateAsync(db, staff.Id.Value, staff.SchoolId, date, ct);

        var summaries = await (
            from cs in db.TcClassSections.AsNoTracking()
            join campus in db.Campuses.AsNoTracking() on cs.CampusId equals campus.CampusId
            join sheet in db.TcClassAttendanceSheets
                    .AsNoTracking()
                    .Where(s => s.SheetDate == date)
                 on cs.ClassSectionId equals sheet.ClassSectionId into sheetGroup
            from sheet in sheetGroup.DefaultIfEmpty()
            where cs.TeacherDcid == teacherDcid && cs.IsActive
            select new ClassSectionSummary
            {
                ClassSectionId = cs.ClassSectionId,
                PsSectionId = cs.PsSectionId,
                CourseName = cs.CourseName,
                CourseNumber = cs.CourseNumber,
                PeriodNumber = cs.PeriodNumber,
                Room = cs.Room,
                Expression = cs.Expression,
                CampusId = cs.CampusId,
                CampusName = campus.CampusName,
                TermName = cs.TermName,
                SchoolYear = cs.SchoolYear,
                EnrolledCount = db.TcSectionEnrollments
                    .Count(e => e.ClassSectionId == cs.ClassSectionId
                             && e.IsActive
                             && e.DateLeft == null),
                Sheet = sheet
            }
        ).ToListAsync(ct);

        return summaries
            .OrderBy(s => s.PeriodNumber ?? int.MaxValue)
            .ThenBy(s => s.CourseName)
            .ToList();
    }

    public async Task<List<EnrollmentRow>> GetRosterAsync(
        int classSectionId, DateOnly date, CancellationToken ct = default)
    {
        using var db = await _dbFactory.CreateDbContextAsync(ct);

        var section = await db.TcClassSections
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ClassSectionId == classSectionId, ct);

        if (section == null) return new();

        if ((DateTime.Now - section.LastSyncDate) > EnrollmentRefreshAfter)
            await MaterializeEnrollmentsAsync(db, section, ct);

        var roster = await db.TcSectionEnrollments
            .AsNoTracking()
            .Where(e => e.ClassSectionId == classSectionId
                     && e.IsActive
                     && (e.DateEnrolled == null || e.DateEnrolled <= date)
                     && (e.DateLeft == null || e.DateLeft >= date))
            .OrderBy(e => e.StudentLastName)
            .ThenBy(e => e.StudentFirstName)
            .Select(e => new EnrollmentRow
            {
                EnrollmentId = e.EnrollmentId,
                StudentDcid = e.StudentDcid,
                StudentNumber = e.StudentNumber,
                FirstName = e.StudentFirstName,
                LastName = e.StudentLastName,
                DateEnrolled = e.DateEnrolled,
                DateLeft = e.DateLeft
            })
            .ToListAsync(ct);

        return roster;
    }

    public Task<TcClassSection?> ResolveCurrentSectionForStudentAsync(
        string studentNumber, DateTime nowLocal, CancellationToken ct = default)
    {
        // Phase E - bell-schedule-driven dispatch lands when the
        // student QR scan flow is built. Service contract reserves the
        // method now so consumers don't break later.
        return Task.FromResult<TcClassSection?>(null);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Internal helpers
    // ─────────────────────────────────────────────────────────────────────

    private async Task<int> EnsureSectionCachedInternalAsync(
        TimeClockDbContext db, long psSectionId, CancellationToken ct)
    {
        var existing = await db.TcClassSections
            .FirstOrDefaultAsync(s => s.PsSectionId == psSectionId, ct);

        var fresh = existing != null
                 && (DateTime.Now - existing.LastSyncDate) < SectionRefreshAfter;
        if (fresh && existing != null)
            return existing.ClassSectionId;

        CmsMasterScheduleRow? cmsRow = null;
        using (var cmsConn = await OpenCmsConnectionAsync(ct))
        using (var cmsCmd = cmsConn.CreateCommand())
        {
            cmsCmd.CommandText = @"
                SELECT TOP 1
                    ms.MasterScheduleID,
                    ms.SchoolID,
                    ms.TermID,
                    ms.SectionID,
                    ms.SectionDcid,
                    ms.CourseNumber,
                    ms.CourseName,
                    ms.SectionNumber,
                    ms.Expression,
                    ms.TranslatedExpression,
                    ms.Room,
                    ms.TeacherID,
                    ms.TeacherName,
                    ms.Department
                FROM dbo.Advising_MasterSchedule ms
                WHERE ms.SectionID = @psSectionId";
            var p = cmsCmd.CreateParameter();
            p.ParameterName = "@psSectionId";
            p.DbType = DbType.Int64;
            p.Value = psSectionId;
            cmsCmd.Parameters.Add(p);

            using var reader = await cmsCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                cmsRow = new CmsMasterScheduleRow(
                    MasterScheduleId:     reader.GetInt32(0),
                    SchoolId:             reader.GetInt32(1),
                    TermId:               reader.GetInt32(2),
                    PsSectionId:          reader.GetInt64(3),
                    PsSectionDcid:        reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    CourseNumber:         reader.GetString(5),
                    CourseName:           reader.GetString(6),
                    SectionNumber:        reader.IsDBNull(7) ? null : reader.GetString(7),
                    Expression:           reader.IsDBNull(8) ? null : reader.GetString(8),
                    TranslatedExpression: reader.IsDBNull(9) ? null : reader.GetString(9),
                    Room:                 reader.IsDBNull(10) ? null : reader.GetString(10),
                    PsTeacherId:          reader.IsDBNull(11) ? null : reader.GetInt32(11),
                    TeacherName:          reader.IsDBNull(12) ? null : reader.GetString(12),
                    Department:           reader.IsDBNull(13) ? null : reader.GetString(13));
            }
        }

        if (cmsRow == null)
        {
            _logger.LogWarning(
                "EnsureSectionCached: PS section {PsSectionId} not found in CMS Advising_MasterSchedule",
                psSectionId);
            if (existing != null) return existing.ClassSectionId;
            throw new InvalidOperationException(
                $"PS section {psSectionId} not found in CMS Advising_MasterSchedule and no local cache exists.");
        }

        var campusId = await db.Campuses
            .Where(c => c.PowerSchoolId == cmsRow.SchoolId)
            .Select(c => (int?)c.CampusId)
            .FirstOrDefaultAsync(ct);

        if (campusId == null)
        {
            throw new InvalidOperationException(
                $"PS section {psSectionId} references SchoolId={cmsRow.SchoolId} but no Attendance_Campuses row matches.");
        }

        int? teacherDcid = null;
        if (cmsRow.PsTeacherId.HasValue)
        {
            teacherDcid = await db.Staff
                .Where(s => s.Id == cmsRow.PsTeacherId.Value
                         && s.SchoolId == cmsRow.SchoolId
                         && s.IsActive)
                .Select(s => (int?)s.Dcid)
                .FirstOrDefaultAsync(ct);

            if (teacherDcid == null)
            {
                _logger.LogInformation(
                    "EnsureSectionCached: PS section {PsSectionId} TeacherID={TeacherId} not yet sync'd into Staff for SchoolId {SchoolId}. Section will be cached without a teacher reference.",
                    psSectionId, cmsRow.PsTeacherId.Value, cmsRow.SchoolId);
            }
        }

        // Resolve TermName + SchoolYear from Advising_TermConfig (best-effort;
        // NULL is acceptable - filter-by-date queries don't need it).
        CmsTermInfo? termInfo = null;
        using (var cmsConn = await OpenCmsConnectionAsync(ct))
        using (var cmsCmd = cmsConn.CreateCommand())
        {
            cmsCmd.CommandText = @"
                SELECT TOP 1 TermName, AcademicYear
                FROM dbo.Advising_TermConfig
                WHERE TermID = @termId
                ORDER BY (CASE WHEN SchoolID = @schoolId THEN 0 ELSE 1 END)";
            var pt = cmsCmd.CreateParameter();
            pt.ParameterName = "@termId";
            pt.DbType = DbType.Int32;
            pt.Value = cmsRow.TermId;
            cmsCmd.Parameters.Add(pt);
            var ps = cmsCmd.CreateParameter();
            ps.ParameterName = "@schoolId";
            ps.DbType = DbType.Int32;
            ps.Value = cmsRow.SchoolId;
            cmsCmd.Parameters.Add(ps);

            using var reader = await cmsCmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                termInfo = new CmsTermInfo(
                    TermName: reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                    AcademicYear: reader.IsDBNull(1) ? string.Empty : reader.GetString(1));
            }
        }

        var periodNumber = ParsePeriodNumber(cmsRow.TranslatedExpression ?? cmsRow.Expression);

        if (existing == null)
        {
            existing = new TcClassSection
            {
                PsSectionId = psSectionId,
                DistrictId = 1,
                CampusId = campusId.Value,
                PsSchoolId = cmsRow.SchoolId,
                CourseNumber = cmsRow.CourseNumber,
                CourseName = cmsRow.CourseName,
                SectionNumber = cmsRow.SectionNumber,
                Expression = cmsRow.Expression,
                PeriodNumber = periodNumber,
                Room = cmsRow.Room,
                TeacherDcid = teacherDcid,
                TermName = NormalizeTermName(termInfo?.TermName),
                SchoolYear = NormalizeSchoolYear(termInfo?.AcademicYear),
                TermId = cmsRow.TermId,
                IsActive = true,
                LastSyncDate = DateTime.Now,
                CreatedDate = DateTime.Now
            };
            db.TcClassSections.Add(existing);
        }
        else
        {
            existing.CampusId = campusId.Value;
            existing.PsSchoolId = cmsRow.SchoolId;
            existing.CourseNumber = cmsRow.CourseNumber;
            existing.CourseName = cmsRow.CourseName;
            existing.SectionNumber = cmsRow.SectionNumber;
            existing.Expression = cmsRow.Expression;
            existing.PeriodNumber = periodNumber;
            existing.Room = cmsRow.Room;
            existing.TeacherDcid = teacherDcid;
            existing.TermName = NormalizeTermName(termInfo?.TermName) ?? existing.TermName;
            existing.SchoolYear = NormalizeSchoolYear(termInfo?.AcademicYear) ?? existing.SchoolYear;
            existing.TermId = cmsRow.TermId;
            existing.IsActive = true;
            existing.LastSyncDate = DateTime.Now;
            existing.ModifiedDate = DateTime.Now;
        }

        await db.SaveChangesAsync(ct);

        await MaterializeEnrollmentsAsync(db, existing, ct);

        return existing.ClassSectionId;
    }

    private async Task SyncTeacherSectionsForDateAsync(
        TimeClockDbContext db, int psTeacherId, long? schoolId, DateOnly date, CancellationToken ct)
    {
        var todayDate = date.ToDateTime(TimeOnly.MinValue);

        var sectionIds = new List<long>();
        using (var cmsConn = await OpenCmsConnectionAsync(ct))
        using (var cmsCmd = cmsConn.CreateCommand())
        {
            cmsCmd.CommandText = @"
                SELECT ms.SectionID
                FROM dbo.Advising_MasterSchedule ms
                INNER JOIN dbo.Advising_TermConfig tc
                    ON tc.TermID = ms.TermID
                WHERE ms.TeacherID = @psTeacherId
                  AND @today BETWEEN tc.StartDate AND tc.EndDate";

            var pt = cmsCmd.CreateParameter();
            pt.ParameterName = "@psTeacherId";
            pt.DbType = DbType.Int32;
            pt.Value = psTeacherId;
            cmsCmd.Parameters.Add(pt);

            var pd = cmsCmd.CreateParameter();
            pd.ParameterName = "@today";
            pd.DbType = DbType.DateTime;
            pd.Value = todayDate;
            cmsCmd.Parameters.Add(pd);

            using var reader = await cmsCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sectionIds.Add(reader.GetInt64(0));
            }
        }

        if (sectionIds.Count == 0)
        {
            _logger.LogInformation(
                "SyncTeacherSectionsForDate: no current-term sections in CMS for PS teacher id {PsTeacherId} on {Date}",
                psTeacherId, date);
            return;
        }

        foreach (var psSectionId in sectionIds)
        {
            try
            {
                await EnsureSectionCachedInternalAsync(db, psSectionId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "SyncTeacherSectionsForDate: failed to materialize section {PsSectionId} for teacher {PsTeacherId}",
                    psSectionId, psTeacherId);
            }
        }
    }

    private async Task MaterializeEnrollmentsAsync(
        TimeClockDbContext db, TcClassSection section, CancellationToken ct)
    {
        // CMS sync 2026-05-28 denormalized SectionID + student name fields
        // onto Advising_StudentSchedule, so this is now a single-column
        // lookup with no JOIN. See [[reference-cms-sync-class-attendance]].
        var cmsEnrollments = new List<CmsStudentScheduleRow>();
        using (var cmsConn = await OpenCmsConnectionAsync(ct))
        using (var cmsCmd = cmsConn.CreateCommand())
        {
            cmsCmd.CommandText = @"
                SELECT
                    ss.StudentScheduleID,
                    ss.StudentDCID,
                    ss.CcDcid,
                    ss.DateEnrolled,
                    ss.DateLeft,
                    ss.StudentFirstName,
                    ss.StudentLastName,
                    ss.StudentNumber
                FROM dbo.Advising_StudentSchedule ss
                WHERE ss.SectionID = @psSectionId";

            var p = cmsCmd.CreateParameter();
            p.ParameterName = "@psSectionId";
            p.DbType = DbType.Int64;
            p.Value = section.PsSectionId;
            cmsCmd.Parameters.Add(p);

            using var reader = await cmsCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                cmsEnrollments.Add(new CmsStudentScheduleRow(
                    StudentScheduleId: reader.GetInt32(0),
                    StudentDcid:       reader.GetInt64(1),
                    CcDcid:            reader.GetInt64(2),
                    DateEnrolled:      reader.IsDBNull(3) ? null : reader.GetDateTime(3),
                    DateLeft:          reader.IsDBNull(4) ? null : reader.GetDateTime(4),
                    StudentFirstName:  reader.IsDBNull(5) ? null : reader.GetString(5),
                    StudentLastName:   reader.IsDBNull(6) ? null : reader.GetString(6),
                    StudentNumber:     reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        var existingByDcid = await db.TcSectionEnrollments
            .Where(e => e.ClassSectionId == section.ClassSectionId)
            .ToDictionaryAsync(e => (e.StudentDcid, e.DateEnrolled), ct);

        foreach (var cms in cmsEnrollments)
        {
            var studentDcid = (int)cms.StudentDcid;
            var dateEnrolled = cms.DateEnrolled.HasValue
                ? DateOnly.FromDateTime(cms.DateEnrolled.Value)
                : (DateOnly?)null;
            var dateLeft = cms.DateLeft.HasValue
                ? DateOnly.FromDateTime(cms.DateLeft.Value)
                : (DateOnly?)null;

            if (existingByDcid.TryGetValue((studentDcid, dateEnrolled), out var existing))
            {
                existing.StudentNumber = cms.StudentNumber ?? existing.StudentNumber;
                existing.StudentLastName = cms.StudentLastName;
                existing.StudentFirstName = cms.StudentFirstName;
                existing.DateLeft = dateLeft;
                existing.LastSyncDate = DateTime.Now;
                existing.ModifiedDate = DateTime.Now;
                existing.IsActive = true;
            }
            else
            {
                db.TcSectionEnrollments.Add(new TcSectionEnrollment
                {
                    ClassSectionId = section.ClassSectionId,
                    StudentDcid = studentDcid,
                    StudentNumber = cms.StudentNumber ?? string.Empty,
                    StudentLastName = cms.StudentLastName,
                    StudentFirstName = cms.StudentFirstName,
                    DistrictId = section.DistrictId,
                    CampusId = section.CampusId,
                    DateEnrolled = dateEnrolled,
                    DateLeft = dateLeft,
                    IsActive = true,
                    LastSyncDate = DateTime.Now,
                    CreatedDate = DateTime.Now
                });
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Format helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Extract period number from PS Expression / TranslatedExpression.
    /// Examples:
    ///   "4(A-C)"   -> 4
    ///   "P4(MW-F)" -> 4
    ///   "8(A-C)"   -> 8
    ///   null       -> null
    /// </summary>
    private static int? ParsePeriodNumber(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return null;
        var trimmed = expression.TrimStart('P', 'p');
        var digits = new string(trimmed.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    /// <summary>
    /// CMS uses short form (T1/T2/T3/T4/T5); TC tables use long form
    /// (TERM1..TERM5). Handles T1-T9 generically so summer term (T5)
    /// and any future term codes follow the same convention.
    /// Matches MasterScheduleLookupService convention.
    /// </summary>
    private static string? NormalizeTermName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim().ToUpperInvariant();

        if (trimmed.Length == 2 && trimmed[0] == 'T' && char.IsDigit(trimmed[1]))
            return "TERM" + trimmed[1];

        return trimmed;
    }

    /// <summary>
    /// CMS stores AcademicYear as 9 chars ("2024-2025"); TC tables store
    /// the 6-char short form ("2024-25"). Convert if needed.
    /// </summary>
    private static string? NormalizeSchoolYear(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (trimmed.Length == 9 && trimmed[4] == '-')
        {
            var startYear = trimmed.Substring(0, 4);
            var endYear = trimmed.Substring(7, 2);
            return $"{startYear}-{endYear}";
        }
        return trimmed;
    }

    // ─────────────────────────────────────────────────────────────────────
    // CMS row records - simple containers populated by hand from
    // SqlDataReader in the OpenCmsConnectionAsync-bound queries above.
    // ─────────────────────────────────────────────────────────────────────

    private record CmsMasterScheduleRow(
        int MasterScheduleId,
        int SchoolId,
        int TermId,
        long PsSectionId,
        long? PsSectionDcid,
        string CourseNumber,
        string CourseName,
        string? SectionNumber,
        string? Expression,
        string? TranslatedExpression,
        string? Room,
        int? PsTeacherId,
        string? TeacherName,
        string? Department);

    private record CmsStudentScheduleRow(
        int StudentScheduleId,
        long StudentDcid,
        long CcDcid,
        DateTime? DateEnrolled,
        DateTime? DateLeft,
        string? StudentFirstName,
        string? StudentLastName,
        string? StudentNumber);

    private record CmsTermInfo(string TermName, string AcademicYear);

    private record TeacherDcidLookupResult(long StaffDcid);
}
