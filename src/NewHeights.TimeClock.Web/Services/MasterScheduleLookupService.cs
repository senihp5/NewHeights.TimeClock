using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Read-only lookup service that finds which teachers are scheduled for a
/// given period on a given date at a given campus. Powers the substitute
/// period picker (SubstituteTimesheetSpec section 5.1): the sub opens the
/// "Add Period" modal, picks session + period, and this service feeds the
/// teacher dropdown with auto-filled course / room / content-area details.
///
/// Day-pattern rules (spec section 6.3):
///   Monday/Wednesday → MW_P1..MW_P6 columns
///   Tuesday/Thursday → TTh_P1..TTh_P6 columns
///   Friday/Saturday/Sunday → no class rows
///
/// Session translation — existing entities disagree on vocabulary:
///   TcMasterSchedule.Shift uses "DAY" / "EVENING"
///   TcSubstitutePeriodEntry.SessionType uses "DAY" / "NIGHT" (per spec)
///   TC_StaffHoursWindow.SessionType uses "DAY" / "NIGHT"
/// This service accepts sessionType "DAY" or "NIGHT" and translates NIGHT -> EVENING
/// when querying master schedule.
/// </summary>
public interface IMasterScheduleLookupService
{
    /// <summary>
    /// Returns the teachers scheduled for <paramref name="periodNumber"/> at
    /// <paramref name="campusId"/> on <paramref name="date"/>, filtered by
    /// session type. Results are denormalized — each slot carries the teacher
    /// display name plus the course / room / content area so the caller can
    /// snapshot them onto a TcSubstitutePeriodEntry without extra joins.
    /// </summary>
    /// <param name="termName">
    /// Optional. When null, the service uses the most recently imported active
    /// term for the campus. Pass explicitly (e.g. "TERM3") to pin the lookup.
    /// </param>
    Task<List<MasterScheduleSlot>> GetTeachersForPeriodAsync(
        int campusId,
        DateOnly date,
        int periodNumber,
        string sessionType = "DAY",
        string? termName = null,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves the term name that the lookup service will default to for the
    /// given campus on <paramref name="asOfDate"/>. When null, uses today.
    /// Reads CaseManagementDB.dbo.Advising_TermConfig (authoritative) and falls
    /// back to the most recently imported active master schedule if unreachable.
    /// Returns null only if both sources fail.
    /// </summary>
    Task<string?> GetCurrentTermNameAsync(int campusId, DateOnly? asOfDate = null, CancellationToken ct = default);
}

/// <summary>
/// Row returned by GetTeachersForPeriodAsync — one per teacher scheduled.
/// TeacherName is safe to display; the others are safe to copy onto a
/// TcSubstitutePeriodEntry (they are snapshots, not live references).
/// </summary>
public class MasterScheduleSlot
{
    public int MasterScheduleId { get; set; }
    public string TeacherName { get; set; } = string.Empty;
    public string? CourseName { get; set; }
    public string? ContentArea { get; set; }
    public string? Room { get; set; }
}

public class MasterScheduleLookupService : IMasterScheduleLookupService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly ILogger<MasterScheduleLookupService> _logger;

    public MasterScheduleLookupService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        ILogger<MasterScheduleLookupService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<List<MasterScheduleSlot>> GetTeachersForPeriodAsync(
        int campusId,
        DateOnly date,
        int periodNumber,
        string sessionType = "DAY",
        string? termName = null,
        CancellationToken ct = default)
    {
        if (periodNumber < 1 || periodNumber > 6)
        {
            _logger.LogWarning("GetTeachersForPeriodAsync called with out-of-range periodNumber {Period}", periodNumber);
            return new List<MasterScheduleSlot>();
        }

        // DayPattern selects MW vs TTh column family. Adult school has no
        // Friday classes — return empty rather than throwing so the picker
        // cleanly shows "no classes scheduled".
        var useMw = IsMondayOrWednesday(date.DayOfWeek);
        var useTth = IsTuesdayOrThursday(date.DayOfWeek);
        if (!useMw && !useTth)
            return new List<MasterScheduleSlot>();

        // Translate caller vocabulary to the storage vocabulary.
        var shiftFilter = NormalizeSessionForMasterSchedule(sessionType);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Resolve current term if caller didn't pin one. Uses the scheduling date
        // (not today's date) so supervisors can look up past / future coverage.
        termName ??= await ResolveCurrentTermAsync(db, campusId, date, ct);
        if (string.IsNullOrWhiteSpace(termName))
        {
            _logger.LogInformation(
                "No active master schedule found for campus {CampusId} — returning empty period lookup", campusId);
            return new List<MasterScheduleSlot>();
        }

        // Pull the full candidate set into memory. The master schedule is small
        // (≤ a few hundred rows per term per campus) so projecting in memory is
        // cheaper than constructing column-dynamic SQL. We still push CampusId,
        // TermName, Shift, and IsActive to the server via Where.
        var rows = await db.TcMasterSchedules
            .AsNoTracking()
            .Include(s => s.Teacher)
            .Where(s => s.IsActive
                     && s.CampusId == campusId
                     && s.TermName == termName
                     && s.Shift == shiftFilter)
            .ToListAsync(ct);

        var results = new List<MasterScheduleSlot>(rows.Count);

        foreach (var row in rows)
        {
            var courseCell = useMw
                ? PickMwCell(row, periodNumber)
                : PickTthCell(row, periodNumber);

            if (string.IsNullOrWhiteSpace(courseCell))
                continue;

            // Unmatched rows (Teacher navigation null) still surface in the UI —
            // the sub shouldn't be blocked by admin data quality issues. We fall
            // back to the raw cell for display in that case.
            var teacherName = FormatTeacherName(row.Teacher) ?? row.RawTeacherCell ?? "(unknown teacher)";

            results.Add(new MasterScheduleSlot
            {
                MasterScheduleId = row.ScheduleId,
                TeacherName      = teacherName,
                CourseName       = courseCell,
                ContentArea      = CourseTagMapper.MapCourseToContentArea(courseCell),
                Room             = row.Room
            });
        }

        // Stable alphabetical order — easier for a sub to scroll a known teacher name.
        return results.OrderBy(s => s.TeacherName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<string?> GetCurrentTermNameAsync(int campusId, DateOnly? asOfDate = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var date = asOfDate ?? DateOnly.FromDateTime(DateTime.Now);
        return await ResolveCurrentTermAsync(db, campusId, date, ct);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<string?> ResolveCurrentTermAsync(
        TimeClockDbContext db, int campusId, DateOnly today, CancellationToken ct)
    {
        // Primary source: CaseManagementDB.dbo.Advising_TermConfig on the same Azure SQL server.
        // The DefaultConnection's login has SELECT permission on both databases, so a
        // 3-part name cross-database query works without a separate DbContext. The
        // interpolation uses EF's FormattableString overload so `today` is parameterized,
        // not string-concatenated.
        try
        {
            var todayDate = today.ToDateTime(TimeOnly.MinValue);
            var term = await db.Database
                .SqlQuery<string>($@"SELECT TOP 1 TermName
                    FROM CaseManagementDB.dbo.Advising_TermConfig
                    WHERE {todayDate} BETWEEN StartDate AND EndDate
                    ORDER BY StartDate DESC")
                .FirstOrDefaultAsync(ct);

            if (!string.IsNullOrWhiteSpace(term))
                return NormalizeTermName(term);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to resolve current term from CaseManagementDB.Advising_TermConfig — falling back to most-recent-import heuristic");
        }

        // Fallback: most recently imported active term for the campus.
        // Triggers when the primary source is unavailable OR when the date
        // falls outside all configured term windows (e.g., a summer break).
        var fallback = await db.TcMasterSchedules
            .Where(s => s.IsActive && s.CampusId == campusId)
            .OrderByDescending(s => s.ImportedDate)
            .Select(s => s.TermName)
            .FirstOrDefaultAsync(ct);
        return fallback is null ? null : NormalizeTermName(fallback);
    }

    // Translate between the two term-name conventions on the same schema:
    //   CaseManagementDB.dbo.Advising_TermConfig.TermName   — short form: T1, T2, T3, T4
    //   TC_MasterSchedule.TermName                          — long form:  TERM1, TERM2, TERM3, TERM4
    // The ScheduleImport UI emits the long form (so do all code paths that write
    // to TcMasterSchedule). Any value coming FROM Advising_TermConfig must be
    // normalized before being used in an equality join on TC_MasterSchedule.TermName,
    // otherwise the WHERE clause matches zero rows and the period picker's teacher
    // dropdown appears empty — the exact symptom that caused this fix on 2026-04-22.
    // Accepts long form unchanged so the function is idempotent.
    private static string NormalizeTermName(string raw)
    {
        var trimmed = raw.Trim().ToUpperInvariant();
        return trimmed switch
        {
            "T1" => "TERM1",
            "T2" => "TERM2",
            "T3" => "TERM3",
            "T4" => "TERM4",
            _    => trimmed
        };
    }

    private static bool IsMondayOrWednesday(DayOfWeek d) =>
        d == DayOfWeek.Monday || d == DayOfWeek.Wednesday;

    private static bool IsTuesdayOrThursday(DayOfWeek d) =>
        d == DayOfWeek.Tuesday || d == DayOfWeek.Thursday;

    // Caller vocabulary: "DAY" / "NIGHT" (matches SubstitutePeriodEntry and StaffHoursWindow).
    // Storage vocabulary on TcMasterSchedule.Shift: "DAY" / "EVENING".
    private static string NormalizeSessionForMasterSchedule(string sessionType)
    {
        if (string.Equals(sessionType, "NIGHT", StringComparison.OrdinalIgnoreCase))
            return "EVENING";
        return "DAY";
    }

    private static string? PickMwCell(TcMasterSchedule row, int periodNumber) => periodNumber switch
    {
        1 => row.MW_P1,
        2 => row.MW_P2,
        3 => row.MW_P3,
        4 => row.MW_P4,
        5 => row.MW_P5,
        6 => row.MW_P6,
        _ => null
    };

    private static string? PickTthCell(TcMasterSchedule row, int periodNumber) => periodNumber switch
    {
        1 => row.TTh_P1,
        2 => row.TTh_P2,
        3 => row.TTh_P3,
        4 => row.TTh_P4,
        5 => row.TTh_P5,
        6 => row.TTh_P6,
        _ => null
    };

    private static string? FormatTeacherName(Staff? staff)
    {
        if (staff is null) return null;
        var first = staff.FirstName?.Trim();
        var last = staff.LastName?.Trim();
        if (string.IsNullOrEmpty(first) && string.IsNullOrEmpty(last)) return null;
        return string.IsNullOrEmpty(first) ? last
             : string.IsNullOrEmpty(last)  ? first
             : $"{first} {last}";
    }
}
