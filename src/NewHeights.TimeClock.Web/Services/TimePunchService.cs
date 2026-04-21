using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Enums;
using NewHeights.TimeClock.Shared.DTOs;

namespace NewHeights.TimeClock.Web.Services;

public interface ITimePunchService
{
    Task<PunchResult> ProcessPunchAsync(PunchRequest request);
    Task<TcTimePunch?> GetLastPunchAsync(int employeeId);
    Task<List<TcTimePunch>> GetTodayPunchesAsync(int employeeId);
    Task<PunchType> DeterminePunchTypeAsync(int employeeId, int campusId);
    Task<TcEmployee?> GetEmployeeByIdNumberAsync(string idNumber);
    Task<decimal> GetTodayHoursAsync(int employeeId);
    string ParseIdFromQrCode(string scannedValue);
}

public class TimePunchService : ITimePunchService
{
    private readonly TimeClockDbContext _context;
    private readonly IGeofenceService _geofenceService;
    private readonly IAuditService _audit;
    private readonly ISubstituteTimesheetService _subTimesheetService;
    private readonly ILogger<TimePunchService> _logger;

    public TimePunchService(
        TimeClockDbContext context,
        IGeofenceService geofenceService,
        IAuditService audit,
        ISubstituteTimesheetService subTimesheetService,
        ILogger<TimePunchService> logger)
    {
        _context = context;
        _geofenceService = geofenceService;
        _audit = audit;
        _subTimesheetService = subTimesheetService;
        _logger = logger;
    }

    public string ParseIdFromQrCode(string scannedValue)
    {
        if (string.IsNullOrWhiteSpace(scannedValue))
            return string.Empty;

        var trimmed = scannedValue.Trim();

        if (trimmed.Contains('|'))
        {
            var parts = trimmed.Split('|');
            if (parts.Length >= 3)
            {
                return parts[2].Trim();
            }
        }

        return trimmed;
    }

    public async Task<TcEmployee?> GetEmployeeByIdNumberAsync(string idNumber)
    {
        return await _context.TcEmployees
            .Include(e => e.Staff)
            .Include(e => e.HomeCampus)
            .Include(e => e.PayRule)
            .FirstOrDefaultAsync(e => e.IdNumber == idNumber && e.IsActive);
    }

    public async Task<TcTimePunch?> GetLastPunchAsync(int employeeId)
    {
        return await _context.TcTimePunches
            .Where(p => p.EmployeeId == employeeId && p.PunchStatus == PunchStatus.Active)
            .OrderByDescending(p => p.PunchDateTime)
            .FirstOrDefaultAsync();
    }

    public async Task<List<TcTimePunch>> GetTodayPunchesAsync(int employeeId)
    {
        var today = DateTime.Now.Date;
        return await _context.TcTimePunches
            .Where(p => p.EmployeeId == employeeId && p.PunchDateTime.Date == today && p.PunchStatus == PunchStatus.Active)
            .OrderBy(p => p.PunchDateTime)
            .ToListAsync();
    }

    public async Task<PunchType> DeterminePunchTypeAsync(int employeeId, int campusId)
    {
        var todayPunches = await GetTodayPunchesAsync(employeeId);
        if (!todayPunches.Any()) return PunchType.In;

        var lastPunch = todayPunches.Last();
        var now = DateTime.Now;
        var campus = await _context.Campuses.FindAsync(campusId);
        var lunchStart = campus?.LunchStartTime?.ToTimeSpan() ?? new TimeSpan(11, 0, 0);
        var lunchEnd = campus?.LunchEndTime?.ToTimeSpan() ?? new TimeSpan(13, 0, 0);
        var currentTime = now.TimeOfDay;
        var hasLunchOut = todayPunches.Any(p => p.PunchType == PunchType.LunchOut);

        return lastPunch.PunchType switch
        {
            PunchType.In when currentTime >= lunchStart && currentTime <= lunchEnd && !hasLunchOut => PunchType.LunchOut,
            PunchType.In => PunchType.Out,
            PunchType.LunchOut => PunchType.LunchIn,
            PunchType.LunchIn => PunchType.Out,
            PunchType.Out => PunchType.In,
            _ => PunchType.In
        };
    }

    public async Task<decimal> GetTodayHoursAsync(int employeeId)
    {
        var punches = await GetTodayPunchesAsync(employeeId);
        if (!punches.Any()) return 0;

        decimal totalMinutes = 0;
        TcTimePunch? lastIn = null;

        foreach (var punch in punches)
        {
            if (punch.PunchType == PunchType.In || punch.PunchType == PunchType.LunchIn)
                lastIn = punch;
            else if ((punch.PunchType == PunchType.Out || punch.PunchType == PunchType.LunchOut) && lastIn != null)
            {
                totalMinutes += (decimal)(punch.PunchDateTime - lastIn.PunchDateTime).TotalMinutes;
                lastIn = null;
            }
        }

        if (lastIn != null)
            totalMinutes += (decimal)(DateTime.Now - lastIn.PunchDateTime).TotalMinutes;

        return Math.Round(totalMinutes / 60, 2);
    }

    public async Task<PunchResult> ProcessPunchAsync(PunchRequest request)
    {
        try
        {
            var idNumber = ParseIdFromQrCode(request.IdNumber);
            _logger.LogInformation("Processing punch - Raw: {Raw}, Parsed ID: {IdNumber}", request.IdNumber, idNumber);

            var employee = await GetEmployeeByIdNumberAsync(idNumber);
            if (employee == null)
            {
                _logger.LogWarning("Punch attempt with unknown ID: {IdNumber}", idNumber);
                return new PunchResult { Success = false, Message = "Badge not registered in system.", ErrorCode = "UNKNOWN_ID" };
            }

            if (!employee.IsActive)
                return new PunchResult { Success = false, Message = "Employee account is inactive", ErrorCode = "INACTIVE" };

            int campusId = request.CampusId;
            if (campusId <= 0 && !string.IsNullOrEmpty(request.CampusCode))
            {
                var campus = await _context.Campuses.FirstOrDefaultAsync(c => c.CampusCode == request.CampusCode);
                campusId = campus?.CampusId ?? employee.HomeCampusId;
            }
            else if (campusId <= 0)
            {
                campusId = employee.HomeCampusId;
            }
            GeofenceResult? geofenceResult = null;

            if (request.Latitude.HasValue && request.Longitude.HasValue)
            {
                geofenceResult = await _geofenceService.ValidateLocationAsync(campusId, request.Latitude.Value, request.Longitude.Value);
            }

            var punchType = await DeterminePunchTypeAsync(employee.EmployeeId, campusId);
            var now = DateTime.Now;
            var roundedTime = CeilingToNextMinute(now);

            var punch = new TcTimePunch
            {
                EmployeeId = employee.EmployeeId,
                CampusId = campusId,
                PunchType = punchType,
                PunchDateTime = now,
                RoundedDateTime = roundedTime,
                Latitude = request.Latitude.HasValue ? (decimal)request.Latitude.Value : null,
                Longitude = request.Longitude.HasValue ? (decimal)request.Longitude.Value : null,
                GeofenceStatus = geofenceResult?.Status ?? GeofenceStatus.Manual,
                VerificationMethod = request.Latitude.HasValue ? "GPS" : "MANUAL",
                ScanMethod = request.ScanMethod ?? "QR",
                QRCodeScanned = request.IdNumber,
                PunchStatus = PunchStatus.Active,
                IsManualEntry = false,
                PunchSubType = request.PunchSubType,
                PunchSource = request.PunchSource ?? AuditSource.Kiosk,
                CreatedDate = DateTime.Now
            };

            _context.TcTimePunches.Add(punch);
            await _context.SaveChangesAsync();

            // Audit: PUNCH_CREATED. Callers pass PunchRequest.PunchSource to label the
            // originating surface (MOBILE, ADMIN_UI, etc). When null, default to KIOSK
            // so the kiosk flow needs no code change.
            // Fires AFTER save so punch.PunchId is populated.
            await _audit.LogActionAsync(
                actionCode: AuditActions.Punch.Created,
                entityType: AuditEntityTypes.Punch,
                entityId: punch.PunchId.ToString(),
                newValues: new
                {
                    punch.PunchId,
                    punch.EmployeeId,
                    punch.CampusId,
                    PunchType = punch.PunchType.ToString(),
                    punch.PunchDateTime,
                    punch.RoundedDateTime,
                    GeofenceStatus = punch.GeofenceStatus.ToString(),
                    punch.ScanMethod,
                    punch.VerificationMethod
                },
                deltaSummary: $"{punchType} punch at campus {campusId} for {employee.Staff?.FullName ?? employee.IdNumber}",
                source: request.PunchSource ?? AuditSource.Kiosk,
                employeeId: employee.EmployeeId,
                campusId: campusId,
                punchId: punch.PunchId);

            // Check for early checkout and flag if needed (server-side, no user prompt)
            if (punchType == PunchType.Out || punchType == PunchType.LunchOut)
            {
                await CheckAndFlagEarlyCheckout(punch, campusId);
            }
            _logger.LogInformation("Punch recorded: Employee={Id}, Type={Type}, Time={Time}", employee.EmployeeId, punchType, now);

            // Phase 7 day-of automation: if a Substitute clocks In, auto-create their
            // TcSubstituteTimecard for the day at this campus and auto-populate any
            // PRE_ASSIGNED period entries from their confirmed TcSubRequest. Walk-in
            // subs (no matching request) get an empty card. Wrapped in try/catch so
            // the auto-flow can never break the primary punch transaction.
            if (punchType == PunchType.In && employee.EmployeeType == EmployeeType.Substitute)
            {
                try
                {
                    await _subTimesheetService.OnKioskCheckInAsync(
                        employee.EmployeeId, campusId, punch.PunchId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Phase 7 auto-flow failed for sub {EmployeeId} at campus {CampusId}. Punch {PunchId} still recorded.",
                        employee.EmployeeId, campusId, punch.PunchId);
                }
            }

            await UpdateDailyTimecardAsync(employee.EmployeeId, campusId, now.Date);
            var totalHours = await GetTodayHoursAsync(employee.EmployeeId);
            var photoBase64 = await GetEmployeePhotoBase64Async(employee.StaffDcid ?? 0);

            var greeting = punchType switch
            {
                PunchType.In => $"Good {GetTimeOfDayGreeting()}, {employee.Staff?.FirstName}!",
                PunchType.Out => $"Goodbye, {employee.Staff?.FirstName}! Total: {totalHours:F2} hours",
                PunchType.LunchOut => $"Enjoy your lunch, {employee.Staff?.FirstName}!",
                PunchType.LunchIn => $"Welcome back, {employee.Staff?.FirstName}!",
                _ => $"Punch recorded, {employee.Staff?.FirstName}!"
            };

            return new PunchResult
            {
                Success = true,
                Message = greeting,
                PunchType = punchType.ToString(),
                PunchTime = now,
                RoundedTime = roundedTime,
                EmployeeName = employee.Staff?.FullName ?? "Unknown",
                EmployeePhotoBase64 = photoBase64,
                NoPhotoOnFile = string.IsNullOrEmpty(photoBase64),
                TotalHoursToday = totalHours,
                GeofenceStatus = geofenceResult?.Status.ToString()
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing punch for: {IdNumber} - {Error}", request.IdNumber, ex.Message);
            return new PunchResult { Success = false, Message = "Error: " + ex.Message, ErrorCode = "SYSTEM_ERROR" };
        }
    }

    private async Task UpdateDailyTimecardAsync(int employeeId, int campusId, DateTime workDate)
    {
        var workDateOnly = DateOnly.FromDateTime(workDate);
        var timecard = await _context.TcDailyTimecards.FirstOrDefaultAsync(t => t.EmployeeId == employeeId && t.WorkDate == workDateOnly);
        var punches = await GetTodayPunchesAsync(employeeId);
        var totalHours = await GetTodayHoursAsync(employeeId);

        var isNewTimecard = false;
        if (timecard == null)
        {
            timecard = new TcDailyTimecard
            {
                EmployeeId = employeeId,
                CampusId = campusId,
                WorkDate = workDateOnly,
                ApprovalStatus = ApprovalStatus.Pending,
                CreatedDate = DateTime.Now
            };
            _context.TcDailyTimecards.Add(timecard);
            isNewTimecard = true;
        }

        timecard.FirstPunchIn = punches.FirstOrDefault(p => p.PunchType == PunchType.In)?.PunchDateTime;
        timecard.LastPunchOut = punches.LastOrDefault(p => p.PunchType == PunchType.Out)?.PunchDateTime;
        timecard.TotalHours = totalHours;
        timecard.RegularHours = Math.Min(totalHours, 8);
        timecard.OvertimeHours = Math.Max(0, totalHours - 8);
        timecard.ModifiedDate = DateTime.Now;

        await _context.SaveChangesAsync();

        // Audit: TIMECARD_CREATED fires only on first-punch-of-day creation. We intentionally
        // do NOT audit every timecard recalculation here — that would produce one audit row
        // per punch, overwhelming the log. TIMECARD_RECALCULATED fires from TimesheetService
        // on explicit recalc requests instead.
        if (isNewTimecard)
        {
            await _audit.LogActionAsync(
                actionCode: AuditActions.Timecard.Created,
                entityType: AuditEntityTypes.Timecard,
                entityId: timecard.TimecardId.ToString(),
                newValues: new
                {
                    timecard.TimecardId,
                    timecard.EmployeeId,
                    timecard.CampusId,
                    timecard.WorkDate,
                    ApprovalStatus = timecard.ApprovalStatus.ToString()
                },
                deltaSummary: $"Daily timecard opened for {workDateOnly:yyyy-MM-dd} on first punch",
                source: AuditSource.System,
                employeeId: employeeId,
                campusId: campusId);
        }
    }

    private async Task<string?> GetEmployeePhotoBase64Async(int staffDcid)
    {
        var photo = await _context.Photos.FirstOrDefaultAsync(p => p.SubjectDcid == staffDcid && p.SubjectType == 1);
        return photo?.PhotoData == null ? null : Convert.ToBase64String(photo.PhotoData);
    }

    /// <summary>
    /// Rounds a punch time UP to the next whole minute (ceiling).
    /// Per business rule (2026-04-20): all timesheet rounding is ceiling-to-the-minute,
    /// applied uniformly to IN and OUT punches. Replaces the previous 15-minute
    /// nearest-interval rounding.
    /// Examples:
    ///   9:00:00.000 -> 9:00:00 (already on minute boundary, no change)
    ///   9:00:00.001 -> 9:01:00
    ///   9:00:30.500 -> 9:01:00
    ///   9:00:59.999 -> 9:01:00
    /// </summary>
    private static DateTime CeilingToNextMinute(DateTime time)
    {
        var truncated = new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, time.Kind);
        return truncated < time ? truncated.AddMinutes(1) : truncated;
    }

    private static string GetTimeOfDayGreeting()
    {
        var hour = DateTime.Now.Hour;
        return hour switch { < 12 => "morning", < 17 => "afternoon", _ => "evening" };
    }

    /// <summary>
    /// Checks if an OUT punch is earlier than expected work hours and flags for supervisor review
    /// </summary>
    private async Task<bool> CheckAndFlagEarlyCheckout(TcTimePunch punch, int campusId)
    {
        if (punch.PunchType != PunchType.Out && punch.PunchType != PunchType.LunchOut)
            return false;

        var punchTime = TimeOnly.FromDateTime(punch.PunchDateTime);
        var dayOfWeek = punch.PunchDateTime.DayOfWeek;

        // Skip weekends
        if (dayOfWeek == DayOfWeek.Saturday || dayOfWeek == DayOfWeek.Sunday)
            return false;

        // Check against staff hours windows
        var windows = await _context.TcStaffHoursWindows.AsNoTracking()
            .Where(w => w.CampusId == campusId && w.IsActive)
            .ToListAsync();

        foreach (var window in windows)
        {
            // Skip Friday night shift
            if (window.SessionType == "NIGHT" && dayOfWeek == DayOfWeek.Friday)
                continue;

            // If punching out during expected work hours, flag it.
            if (punchTime >= window.ExpectedArrivalTime && punchTime < window.ExpectedDepartureTime)
            {
                // Preserve a user-provided reason (LUNCH / MEDICAL / MEETING / PERSONAL /
                // EMERGENCY) from the mobile early-out modal — it's strictly more informative
                // than the generic EARLY_OUT marker. Only auto-label when nothing is set.
                var userProvidedReason = punch.PunchSubType;
                bool appliedGenericFlag = false;
                if (string.IsNullOrEmpty(userProvidedReason))
                {
                    punch.PunchSubType = "EARLY_OUT";
                    _context.TcTimePunches.Update(punch);
                    await _context.SaveChangesAsync();
                    appliedGenericFlag = true;
                }

                _logger.LogInformation("Early checkout flagged for supervisor review: Employee={Id}, Time={Time}, Reason={Reason}",
                                    punch.EmployeeId, punch.PunchDateTime, punch.PunchSubType);

                // Audit: PUNCH_EARLY_OUT_FLAG — lets supervisors filter on "what went home early today"
                // without having to scan every punch. Source=SYSTEM because the flag is set by
                // the rule engine, not by a user action. Fires regardless of whether we set the
                // generic EARLY_OUT marker or kept the user-provided reason, so the supervisor
                // filter stays complete.
                await _audit.LogActionAsync(
                    actionCode: AuditActions.Punch.EarlyOutFlag,
                    entityType: AuditEntityTypes.Punch,
                    entityId: punch.PunchId.ToString(),
                    newValues: new
                    {
                        punch.PunchId,
                        FinalPunchSubType = punch.PunchSubType,
                        UserProvidedReason = userProvidedReason,
                        AppliedGenericFlag = appliedGenericFlag,
                        punch.PunchDateTime,
                        ExpectedDeparture = window.ExpectedDepartureTime,
                        window.SessionType
                    },
                    deltaSummary: $"Early OUT at {punchTime} (expected ≥ {window.ExpectedDepartureTime}, session {window.SessionType}, reason {punch.PunchSubType ?? "UNSPECIFIED"})",
                    source: AuditSource.System,
                    employeeId: punch.EmployeeId,
                    campusId: campusId,
                    punchId: punch.PunchId);

                return true;
            }
        }

        return false;
    }}