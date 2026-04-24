using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.VisualBasic.FileIO;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Parses Google-Form weekly-timesheet CSV exports (Jasmine / Karina / Griselda /
/// Leighla style) into a set of suggested IN/OUT punch plans, then applies the
/// admin-approved plan by writing TC_TimePunches rows and triggering per-date
/// timecard recalc. See memory reference_paper_timesheet_csv_formats.md for the
/// column-layout + cell-value conventions handled here.
/// </summary>
public interface IHourlyCsvImportService
{
    Task<CsvParseResult> ParseAsync(Stream stream, string fileName, CancellationToken ct = default);
    Task<CsvApplyResult> ApplyAsync(int employeeId, int campusId, List<PunchPlanRow> rows, string appliedBy, CancellationToken ct = default);
}

public class HourlyCsvImportService : IHourlyCsvImportService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly ITimesheetService _timesheetService;
    private readonly IPayPeriodService _payPeriodService;
    private readonly IAuditService _audit;
    private readonly ILogger<HourlyCsvImportService> _logger;

    public HourlyCsvImportService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        ITimesheetService timesheetService,
        IPayPeriodService payPeriodService,
        IAuditService audit,
        ILogger<HourlyCsvImportService> logger)
    {
        _dbFactory = dbFactory;
        _timesheetService = timesheetService;
        _payPeriodService = payPeriodService;
        _audit = audit;
        _logger = logger;
    }

    // ── PARSE ────────────────────────────────────────────────────────────

    public async Task<CsvParseResult> ParseAsync(Stream stream, string fileName, CancellationToken ct = default)
    {
        var result = new CsvParseResult { SourceFileName = fileName };

        // Read all fields as strings via a quote-aware parser so multi-line
        // "Assurance Statement" header values don't corrupt row counts.
        var records = new List<string[]>();
        using (var parser = new TextFieldParser(stream) { TextFieldType = FieldType.Delimited, HasFieldsEnclosedInQuotes = true })
        {
            parser.SetDelimiters(",");
            while (!parser.EndOfData)
            {
                var fields = parser.ReadFields();
                if (fields != null) records.Add(fields);
            }
        }

        if (records.Count < 2)
        {
            result.Error = "CSV had no data rows.";
            return result;
        }

        var header = records[0];
        var dataRows = records.Skip(1).Where(r => r.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();

        // ── Column index resolution ──────────────────────────────────────
        int IndexOf(params string[] hints) => Array.FindIndex(header, h =>
            h != null && hints.Any(hint => h.Contains(hint, StringComparison.OrdinalIgnoreCase)));

        var tsIdx           = IndexOf("Timestamp");
        var nameIdx         = IndexOf("Name");
        var dateIdx         = IndexOf("Today's Date", "Today'sDate");
        var amIdx           = IndexOf("AM Attendance");
        var pmIdx           = IndexOf("PM Attendance");
        var eveIdx          = IndexOf("Evening Attendance");
        var commentIdx      = IndexOf("Comments");
        var hoursWorkedIdx  = IndexOf("Hours Worked");
        var totalHoursIdx   = IndexOf("Total Hours");
        var holIdx          = IndexOf("Holiday Hours");

        if (dateIdx < 0 || (hoursWorkedIdx < 0 && totalHoursIdx < 0) || (amIdx < 0 && pmIdx < 0 && eveIdx < 0))
        {
            result.Error = "Could not find expected columns (Today's Date, at least one of Hours Worked/Total Hours, and at least one attendance column).";
            return result;
        }

        // Parse the scheduled windows from whatever attendance headers are present.
        (TimeOnly Start, TimeOnly End)? amWindow  = amIdx  >= 0 ? ParseWindow(header[amIdx])  : null;
        (TimeOnly Start, TimeOnly End)? pmWindow  = pmIdx  >= 0 ? ParseWindow(header[pmIdx])  : null;
        (TimeOnly Start, TimeOnly End)? eveWindow = eveIdx >= 0 ? ParseWindow(header[eveIdx]) : null;

        result.ScheduledAm = amWindow;
        result.ScheduledPm = pmWindow;
        result.ScheduledEvening = eveWindow;

        // Employee name — take first non-empty value.
        result.EmployeeName = dataRows
            .Select(r => nameIdx >= 0 && nameIdx < r.Length ? r[nameIdx]?.Trim() : null)
            .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "(unknown)";

        // Try to resolve the employee by name against TcEmployees (inexact best-guess).
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var needle = result.EmployeeName.Trim().ToUpperInvariant();
            var candidates = await db.TcEmployees
                .AsNoTracking()
                .Include(e => e.Staff)
                .Where(e => e.IsActive)
                .ToListAsync(ct);

            var hit = candidates.FirstOrDefault(e =>
                   string.Equals((e.DisplayName ?? "").Trim(), needle, StringComparison.OrdinalIgnoreCase)
                || string.Equals(((e.Staff?.FirstName + " " + e.Staff?.LastName) ?? "").Trim(), needle, StringComparison.OrdinalIgnoreCase));
            if (hit == null)
            {
                // Last-name match as a soft fallback.
                var lastWord = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
                hit = candidates.FirstOrDefault(e =>
                    string.Equals((e.Staff?.LastName ?? "").Trim(), lastWord, StringComparison.OrdinalIgnoreCase));
            }
            result.ResolvedEmployeeId = hit?.EmployeeId;
            result.ResolvedCampusId   = hit?.HomeCampusId;
        }

        // ── Build punch plans per row ────────────────────────────────────
        foreach (var row in dataRows)
        {
            if (row.Length <= dateIdx) continue;
            var rawDate = row[dateIdx]?.Trim();
            if (string.IsNullOrWhiteSpace(rawDate)) continue;
            if (!DateOnly.TryParse(rawDate, CultureInfo.CurrentCulture, DateTimeStyles.None, out var workDate))
                if (!DateOnly.TryParse(rawDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out workDate))
                    continue;

            var amCell       = amIdx       >= 0 && amIdx       < row.Length ? row[amIdx]?.Trim()       : null;
            var pmCell       = pmIdx       >= 0 && pmIdx       < row.Length ? row[pmIdx]?.Trim()       : null;
            var eveCell      = eveIdx      >= 0 && eveIdx      < row.Length ? row[eveIdx]?.Trim()      : null;
            var commentCell  = commentIdx  >= 0 && commentIdx  < row.Length ? row[commentIdx]?.Trim()  : null;
            var workedCell   = hoursWorkedIdx >= 0 && hoursWorkedIdx < row.Length ? row[hoursWorkedIdx]?.Trim() : null;
            var totalCell    = totalHoursIdx  >= 0 && totalHoursIdx  < row.Length ? row[totalHoursIdx]?.Trim()  : null;
            var holidayCell  = holIdx      >= 0 && holIdx      < row.Length ? row[holIdx]?.Trim()      : null;

            // Prefer "Hours Worked" when present. Some older forms (e.g., Jasmine's
            // Evening-only template) leave "Hours Worked" blank and stuff the real
            // value into "Total Hours" instead — fall back there.
            var workedParsed = ParseHoursText(workedCell);
            var totalParsed  = ParseHoursText(totalCell);
            var hoursWorked  = workedParsed > 0 ? workedParsed : totalParsed;
            var holidayHours = ParseHoursText(holidayCell);

            var plan = new PunchPlanRow
            {
                WorkDate      = workDate,
                HoursWorked   = hoursWorked,
                HolidayHours  = holidayHours,
                Notes         = commentCell,
                AmRaw         = amCell,
                PmRaw         = pmCell,
                EveRaw        = eveCell,
                Reason        = DetectReasonFromCells(amCell, pmCell, eveCell, holidayHours, commentCell)
            };

            // Suggest IN/OUT per session by combining cell value + schedule.
            // Priority: literal time range in cell > "In office" → scheduled window > "Not Applicable" → skip session.
            (plan.AmIn, plan.AmOut) = InterpretSession(amCell, amWindow, hoursWorked);
            (plan.PmIn, plan.PmOut) = InterpretSession(pmCell, pmWindow, hoursWorked);
            (plan.EveIn, plan.EveOut) = InterpretSession(eveCell, eveWindow, hoursWorked);

            // Split-schedule correction: if hours > scheduled sum and both AM+PM are "In office",
            // extend PM end to absorb the extra hours.
            if (amWindow.HasValue && pmWindow.HasValue
                && plan.AmIn.HasValue && plan.AmOut.HasValue
                && plan.PmIn.HasValue && plan.PmOut.HasValue)
            {
                var scheduledTotal = (decimal)((amWindow.Value.End - amWindow.Value.Start).TotalHours
                                              + (pmWindow.Value.End - pmWindow.Value.Start).TotalHours);
                if (hoursWorked > 0 && hoursWorked != scheduledTotal)
                {
                    // AM fills its scheduled window exactly; extras / shortages on PM end.
                    plan.AmIn  = amWindow.Value.Start;
                    plan.AmOut = amWindow.Value.End;
                    plan.PmIn  = pmWindow.Value.Start;
                    var pmHours = hoursWorked - (decimal)((amWindow.Value.End - amWindow.Value.Start).TotalHours);
                    if (pmHours > 0)
                        plan.PmOut = pmWindow.Value.Start.AddHours((double)pmHours);
                    else
                        plan.PmIn = plan.PmOut = null;
                }
            }

            // Skip-logic: no usable session and no hours → skip
            if (plan.HoursWorked <= 0
                && plan.AmIn == null && plan.PmIn == null && plan.EveIn == null
                && holidayHours <= 0)
            {
                plan.Skip = true;
                plan.SkipReason = "0 hours (holiday/break/non-work day)";
            }

            result.Rows.Add(plan);
        }

        return result;
    }

    // ── APPLY ────────────────────────────────────────────────────────────

    public async Task<CsvApplyResult> ApplyAsync(int employeeId, int campusId, List<PunchPlanRow> rows, string appliedBy, CancellationToken ct = default)
    {
        var apply = new CsvApplyResult();
        var datesTouched = new HashSet<DateOnly>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        foreach (var row in rows.Where(r => !r.Skip))
        {
            try
            {
                var created = new List<TcTimePunch>();

                TcTimePunch? MakePair(TimeOnly? inTime, TimeOnly? outTime, string sessionLabel)
                {
                    if (!inTime.HasValue || !outTime.HasValue) return null;
                    var inDt  = row.WorkDate.ToDateTime(inTime.Value);
                    var outDt = row.WorkDate.ToDateTime(outTime.Value);
                    if (outDt <= inDt) return null;

                    var pIn = new TcTimePunch
                    {
                        EmployeeId = employeeId,
                        CampusId   = campusId,
                        PunchType  = PunchType.In,
                        PunchDateTime = inDt,
                        RoundedDateTime = inDt,
                        PunchStatus = PunchStatus.Active,
                        IsManualEntry = true,
                        IsAutoCheckout = false,
                        VerificationMethod = "SYSTEM",
                        ScanMethod = "IMPORT",
                        PunchSource = "CSV_IMPORT",
                        GeofenceStatus = GeofenceStatus.Manual,
                        SessionType = InferSessionType(inTime.Value),
                        Notes = string.IsNullOrWhiteSpace(row.Reason)
                            ? $"Paper timesheet import ({sessionLabel}); source row={row.WorkDate:yyyy-MM-dd}"
                            : $"Paper timesheet import ({sessionLabel}); reason={row.Reason}; source row={row.WorkDate:yyyy-MM-dd}",
                        CreatedDate = DateTime.Now
                    };
                    var pOut = new TcTimePunch
                    {
                        EmployeeId = employeeId,
                        CampusId   = campusId,
                        PunchType  = PunchType.Out,
                        PunchDateTime = outDt,
                        RoundedDateTime = outDt,
                        PunchStatus = PunchStatus.Active,
                        IsManualEntry = true,
                        IsAutoCheckout = false,
                        VerificationMethod = "SYSTEM",
                        ScanMethod = "IMPORT",
                        PunchSource = "CSV_IMPORT",
                        GeofenceStatus = GeofenceStatus.Manual,
                        SessionType = InferSessionType(inTime.Value),
                        Notes = string.IsNullOrWhiteSpace(row.Reason)
                            ? $"Paper timesheet import ({sessionLabel}); source row={row.WorkDate:yyyy-MM-dd}"
                            : $"Paper timesheet import ({sessionLabel}); reason={row.Reason}; source row={row.WorkDate:yyyy-MM-dd}",
                        CreatedDate = DateTime.Now
                    };
                    db.TcTimePunches.Add(pIn);
                    db.TcTimePunches.Add(pOut);
                    created.Add(pIn);
                    created.Add(pOut);
                    return pIn;
                }

                MakePair(row.AmIn,  row.AmOut,  "AM");
                MakePair(row.PmIn,  row.PmOut,  "PM");
                MakePair(row.EveIn, row.EveOut, "Evening");

                await db.SaveChangesAsync(ct);

                // Pair each IN with its OUT (since we added them in pairs, adjacent entries)
                for (int i = 0; i < created.Count; i += 2)
                {
                    if (i + 1 >= created.Count) break;
                    var pIn  = created[i];
                    var pOut = created[i + 1];
                    pIn.PairedPunchId  = pOut.PunchId;
                    pOut.PairedPunchId = pIn.PunchId;
                }
                await db.SaveChangesAsync(ct);

                foreach (var p in created)
                {
                    await _audit.LogActionAsync(
                        actionCode: AuditActions.Punch.ManualEntry,
                        entityType: AuditEntityTypes.Punch,
                        entityId: p.PunchId.ToString(),
                        newValues: new { p.EmployeeId, p.CampusId, p.PunchType, p.PunchDateTime, Source = "CSV_IMPORT", AppliedBy = appliedBy },
                        deltaSummary: $"Paper CSV import: {p.PunchType} at {p.PunchDateTime:yyyy-MM-dd HH:mm}",
                        source: AuditSource.AdminUi,
                        employeeId: p.EmployeeId,
                        campusId: p.CampusId,
                        punchId: p.PunchId);
                }

                apply.RowsApplied++;
                apply.PunchesCreated += created.Count;
                datesTouched.Add(row.WorkDate);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CSV apply failed for row {Date}", row.WorkDate);
                apply.Errors.Add($"{row.WorkDate:yyyy-MM-dd}: {ex.Message}");
            }
        }

        // Recalc each affected date's daily timecard
        foreach (var d in datesTouched)
        {
            try
            {
                await _timesheetService.RecalculateDailyTimecardAsync(employeeId, d);
            }
            catch (Exception ex)
            {
                apply.Errors.Add($"Recalc {d:yyyy-MM-dd}: {ex.Message}");
            }
        }

        // Migration 052: write ShortDayReason + ShortDayNote for rows tagged with
        // a reason. Runs AFTER recalc so the daily row exists. Also handles skipped
        // rows with a reason (zero-punch day like Weather Closure or Holiday).
        foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r.Reason)))
        {
            try
            {
                await _timesheetService.SetShortDayReasonAsync(
                    employeeId, row.WorkDate, row.Reason, row.Notes, appliedBy);
            }
            catch (Exception ex)
            {
                apply.Errors.Add($"Reason {row.WorkDate:yyyy-MM-dd}: {ex.Message}");
            }
        }

        // Retroactive employee-approval: paper timesheets are already signed by the
        // employee, so imported historical weeks should land in the supervisor's
        // queue as "employee-approved" automatically. We skip the current pay
        // period — the employee still owns approval for the in-progress week.
        // Mondays are the conventional week-start; SubmitTimesheetAsync normalizes
        // to Monday internally, so any day in the week works.
        try
        {
            var currentPeriod = await _payPeriodService.GetPayPeriodForDateAsync(DateTime.Today);
            var currentPeriodStart = currentPeriod?.StartDate;

            var weekKeys = datesTouched
                .Select(d => FirstMondayOnOrBefore(d))
                .Distinct()
                .ToList();

            foreach (var weekStart in weekKeys)
            {
                var weekEnd = weekStart.AddDays(6);

                // Skip if ANY day in this week falls on or after the current pay
                // period's start — that's still "active" from the employee's
                // perspective and they must approve it themselves.
                if (currentPeriodStart.HasValue && weekEnd >= currentPeriodStart.Value)
                {
                    _logger.LogInformation(
                        "CSV import: skipping retroactive approval for week {WeekStart} (overlaps current pay period {PeriodStart})",
                        weekStart, currentPeriodStart);
                    continue;
                }

                await _timesheetService.SubmitTimesheetAsync(employeeId, weekEnd, appliedBy);
                apply.RetroactiveWeeksApproved++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Retroactive employee-approval failed; import still succeeded.");
            apply.Errors.Add($"Retroactive approval: {ex.Message}");
        }

        return apply;
    }

    private static DateOnly FirstMondayOnOrBefore(DateOnly d)
    {
        while (d.DayOfWeek != DayOfWeek.Monday) d = d.AddDays(-1);
        return d;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    // Header examples: "AM Attendance (8 am - noon)", "PM Attendance (12:45 pm - 4:45pm)", "Evening Attendance (4:45 pm - 9:00 pm)"
    private static (TimeOnly Start, TimeOnly End)? ParseWindow(string header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var m = Regex.Match(header, @"\(([^)]+)\)");
        if (!m.Success) return null;
        return ParseTimeRange(m.Groups[1].Value);
    }

    // "7:45 am - 11:45 am", "8 am - noon", "4:45 pm - 9:15 pm", "12:35PM-2:35"
    private static (TimeOnly Start, TimeOnly End)? ParseTimeRange(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var parts = Regex.Split(raw.Trim(), @"\s*-\s*");
        if (parts.Length != 2) return null;

        var start = ParseLooseTime(parts[0]);
        var end   = ParseLooseTime(parts[1]);
        if (start == null || end == null) return null;

        // If end < start, and end has no am/pm marker, assume it shares the start's marker.
        if (end.Value < start.Value && !HasAmPmMarker(parts[1]))
            end = start.Value.AddHours(12) < start.Value ? end : end.Value.AddHours(12);

        return (start.Value, end.Value);
    }

    private static bool HasAmPmMarker(string s) =>
        Regex.IsMatch(s, @"\b(am|pm|AM|PM)\b|noon|midnight", RegexOptions.IgnoreCase);

    // Accepts "8", "8am", "8 am", "8:30am", "noon", "12 pm", "4:45pm", "12:35PM"
    private static TimeOnly? ParseLooseTime(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Equals("noon", StringComparison.OrdinalIgnoreCase)) return new TimeOnly(12, 0);
        if (s.Equals("midnight", StringComparison.OrdinalIgnoreCase)) return new TimeOnly(0, 0);

        // Normalize "8am" → "8 am"
        s = Regex.Replace(s, @"(?i)(\d)(am|pm)", "$1 $2");

        var formats = new[]
        {
            "h:mm tt", "h tt", "h:mmtt", "htt",
            "HH:mm", "H:mm", "HH", "H",
            "h:mm", "hmm"
        };
        foreach (var fmt in formats)
        {
            if (TimeOnly.TryParseExact(s, fmt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                return t;
        }
        return TimeOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t2) ? t2 : null;
    }

    // Hours cell: "4.25", "8", "9 hours 15 min", "4 hours and 15 min"
    private static decimal ParseHoursText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0m;
        raw = raw.Trim();
        if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
            return d;

        var h = 0m; var m = 0m;
        var hm = Regex.Match(raw, @"(\d+(?:\.\d+)?)\s*(?:hours?|hrs?|h)", RegexOptions.IgnoreCase);
        if (hm.Success) h = decimal.Parse(hm.Groups[1].Value, CultureInfo.InvariantCulture);
        var mm = Regex.Match(raw, @"(\d+)\s*(?:minutes?|mins?|m)\b", RegexOptions.IgnoreCase);
        if (mm.Success) m = decimal.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);

        return h + (m / 60m);
    }

    // Cell value decides the (in, out) for one session.
    //   "In office"      → scheduled window
    //   "Not Applicable" → (null, null) — did not work this session
    //   "School Holiday" → (null, null)
    //   "12:35PM-2:35"   → parsed literal range
    //   anything else    → best-effort literal parse
    private static (TimeOnly? In, TimeOnly? Out) InterpretSession(
        string? cell, (TimeOnly Start, TimeOnly End)? scheduled, decimal hoursWorked)
    {
        if (string.IsNullOrWhiteSpace(cell)) return (null, null);
        var t = cell.Trim();

        if (t.Equals("Not Applicable", StringComparison.OrdinalIgnoreCase)) return (null, null);
        if (Regex.IsMatch(t, @"(holiday|break)", RegexOptions.IgnoreCase))  return (null, null);

        if (t.Equals("In office", StringComparison.OrdinalIgnoreCase))
        {
            if (scheduled.HasValue)
                return (scheduled.Value.Start, scheduled.Value.End);
            return (null, null);
        }

        // Literal time range in the cell (e.g. "4:45 pm - 9:15 pm" or "12:35PM-2:35")
        var range = ParseTimeRange(t);
        if (range.HasValue) return (range.Value.Start, range.Value.End);

        // Default: scheduled window if present
        return scheduled.HasValue ? (scheduled.Value.Start, scheduled.Value.End) : (null, null);
    }

    // Scan cell values + comments for keywords that indicate a known reason
    // for a short / no-work day. Admin can override via the dropdown on the
    // preview — this just sets a sensible default so Griselda's "School Holiday"
    // rows arrive pre-tagged as Holiday, weather mentions become WeatherClosure, etc.
    private static string DetectReasonFromCells(string? am, string? pm, string? eve, decimal holidayHours, string? comment)
    {
        var joined = ((am ?? "") + " " + (pm ?? "") + " " + (eve ?? "") + " " + (comment ?? ""))
                        .ToLowerInvariant();
        if (joined.Contains("weather") || joined.Contains("snow day") || joined.Contains("ice day"))
            return "WeatherClosure";
        if (joined.Contains("holiday") || joined.Contains("winter break") || joined.Contains("spring break")
            || joined.Contains("thanksgiving") || holidayHours > 0)
            return "Holiday";
        if (joined.Contains("pto") || joined.Contains("vacation"))
            return "PTO";
        if (joined.Contains("sick"))
            return "Sick";
        if (joined.Contains("prof") && joined.Contains("dev"))
            return "ProfessionalDev";
        if (joined.Contains(" pd ") || joined.StartsWith("pd ") || joined.EndsWith(" pd"))
            return "ProfessionalDev";
        if (joined.Contains("personal"))
            return "Personal";
        return string.Empty;
    }

    // SessionType on TcTimePunch: DAY or NIGHT — derived from in-time boundary.
    // Cut-over is 5 PM per the app's existing session-derivation pattern.
    private static string InferSessionType(TimeOnly inTime)
        => inTime.Hour >= 17 ? "NIGHT" : "DAY";
}

// ── Result DTOs ───────────────────────────────────────────────────────────

public class CsvParseResult
{
    public string SourceFileName { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public int? ResolvedEmployeeId { get; set; }
    public int? ResolvedCampusId { get; set; }
    public (TimeOnly Start, TimeOnly End)? ScheduledAm { get; set; }
    public (TimeOnly Start, TimeOnly End)? ScheduledPm { get; set; }
    public (TimeOnly Start, TimeOnly End)? ScheduledEvening { get; set; }
    public List<PunchPlanRow> Rows { get; set; } = new();
    public string? Error { get; set; }
}

public class PunchPlanRow
{
    public DateOnly WorkDate { get; set; }
    public string? AmRaw { get; set; }
    public string? PmRaw { get; set; }
    public string? EveRaw { get; set; }
    public TimeOnly? AmIn { get; set; }
    public TimeOnly? AmOut { get; set; }
    public TimeOnly? PmIn { get; set; }
    public TimeOnly? PmOut { get; set; }
    public TimeOnly? EveIn { get; set; }
    public TimeOnly? EveOut { get; set; }
    public decimal HoursWorked { get; set; }
    public decimal HolidayHours { get; set; }
    public string? Notes { get; set; }
    public bool Skip { get; set; }
    public string? SkipReason { get; set; }

    /// <summary>
    /// Reason for short-hours / partial / zero-punch days, selected by the admin
    /// on the import preview. Used when the paper timesheet documents a
    /// legitimate non-work or reduced day (Weather Closure, PTO, Sick, Holiday,
    /// etc.). Written to the punch Notes and into the recalculated daily
    /// timecard's ExceptionNotes so HR sees why the hours are under schedule.
    /// Empty string = no reason / full normal day.
    /// </summary>
    public string Reason { get; set; } = string.Empty;
}

public class CsvApplyResult
{
    public int RowsApplied { get; set; }
    public int PunchesCreated { get; set; }
    public int RetroactiveWeeksApproved { get; set; }
    public List<string> Errors { get; set; } = new();
}
