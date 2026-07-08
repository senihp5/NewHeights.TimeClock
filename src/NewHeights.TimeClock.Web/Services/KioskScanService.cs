using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Audit;
using NewHeights.TimeClock.Shared.Constants;
using NewHeights.TimeClock.Shared.DTOs;
using NewHeights.TimeClock.Shared.Enums;
using NewHeights.TimeClock.Web.Hubs;

namespace NewHeights.TimeClock.Web.Services;

public class KioskScanService : IKioskScanService
{
    private readonly IDbContextFactory<TimeClockDbContext> _dbFactory;
    private readonly ITimePunchService _timePunchService;
    private readonly ILogger<KioskScanService> _logger;
    private readonly IHubContext<DashboardHub> _hubContext;

    public KioskScanService(
        IDbContextFactory<TimeClockDbContext> dbFactory,
        ITimePunchService timePunchService,
        ILogger<KioskScanService> logger,
        IHubContext<DashboardHub> hubContext)
    {
        _dbFactory = dbFactory;
        _timePunchService = timePunchService;
        _logger = logger;
        _hubContext = hubContext;
    }

    public async Task<KioskScanResult> ProcessRawScanAsync(string rawScan, int campusId, string scanMethod,
        int terminalId = 0, int locationId = 1)
    {
        if (string.IsNullOrWhiteSpace(rawScan))
        {
            return Error("Empty scan", "EMPTY_SCAN");
        }

        var (firstName, lastName, idNumber) = ParseScanData(rawScan);
        _logger.LogInformation("KioskScan - Raw: {Raw} Parsed: {First} {Last} ID:{Id} Source:{Source} Terminal:{Terminal} Location:{Location}",
            rawScan, firstName, lastName, idNumber, scanMethod, terminalId, locationId);

        if (string.IsNullOrWhiteSpace(idNumber))
        {
            return Error("Invalid scan format", "INVALID_FORMAT");
        }

        using var context = await _dbFactory.CreateDbContextAsync();

        // 2026-07-08: Per-person 60-second min-time-between-scans. Patrick
        // observed a 1:15pm OUT-then-IN bounce on the tablet — the client-side
        // 3s debounce is fine for rapid-frame duplicates but doesn't cover
        // slower re-scans (badge lingering in viewfinder, user stepping away
        // and back, curiosity re-scan). This server-side gate covers every
        // caller (tablet, reception ClockInOut, ESP32, mobile) with one fix.
        // Matches padded/unpadded variants because Staff / TC_Employees
        // sometimes carry 6-digit IdNumbers ('000139') while badge QRs
        // usually have the unpadded form ('139').
        {
            var debouncePadded = idNumber.PadLeft(6, '0');
            var debounceUnpadded = idNumber.TrimStart('0');
            var threshold = DateTime.Now.AddSeconds(-60);
            var lastScanAt = await context.AttendanceTransactions.AsNoTracking()
                .Where(t => t.ScanDateTime >= threshold &&
                            (t.IdNumber == idNumber
                             || t.IdNumber == debouncePadded
                             || t.IdNumber == debounceUnpadded))
                .OrderByDescending(t => t.ScanDateTime)
                .Select(t => (DateTime?)t.ScanDateTime)
                .FirstOrDefaultAsync();

            if (lastScanAt.HasValue)
            {
                var secondsSince = (DateTime.Now - lastScanAt.Value).TotalSeconds;
                if (secondsSince < 60)
                {
                    var waitSeconds = 60 - (int)secondsSince;
                    _logger.LogInformation(
                        "KioskScan REJECTED (TOO_SOON): ID {IdNumber} scanned {Delta:F1}s ago at terminal {Terminal}",
                        idNumber, secondsSince, terminalId);
                    return Error(
                        $"Already scanned. Please wait {waitSeconds}s before scanning again.",
                        "TOO_SOON");
                }
            }
        }

        bool hasNameFromScan = !string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName);

        string paddedIdNumber = idNumber.PadLeft(6, '0');
        string unpaddedIdNumber = idNumber.TrimStart('0');

        Staff? staff = await context.Staff.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsActive &&
                (s.IdNumber == idNumber || s.IdNumber == paddedIdNumber || s.IdNumber == unpaddedIdNumber));

        if (staff != null && hasNameFromScan)
        {
            bool nameMatches = string.Equals(staff.FirstName, firstName, StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(staff.LastName, lastName, StringComparison.OrdinalIgnoreCase);
            if (!nameMatches)
            {
                var byName = await context.Staff.AsNoTracking()
                    .FirstOrDefaultAsync(s => s.IdNumber == idNumber && s.IsActive &&
                        s.FirstName != null && s.LastName != null &&
                        s.FirstName.ToLower() == firstName.ToLower() &&
                        s.LastName.ToLower() == lastName.ToLower());
                if (byName != null) staff = byName;
            }
        }

        if (staff != null)
        {
            var hourlyEmployee = await context.TcEmployees.AsNoTracking()
                .FirstOrDefaultAsync(e => e.IsActive &&
                    (e.IdNumber == idNumber || e.IdNumber == paddedIdNumber) &&
                    (e.EmployeeType == EmployeeType.HourlyStaff
                     || e.EmployeeType == EmployeeType.HourlyPartTime
                     || e.EmployeeType == EmployeeType.Substitute));

            KioskScanResult result = hourlyEmployee != null
                ? await ProcessHourlyAsync(staff, hourlyEmployee, campusId, scanMethod, terminalId, locationId, context)
                : await ProcessStaffAsync(staff, campusId, scanMethod, terminalId, locationId, context);

            if (result.Success) await NotifyDashboardAsync(campusId);
            return result;
        }

        Student? student = hasNameFromScan
            ? await context.Students.AsNoTracking()
                .FirstOrDefaultAsync(s => s.StudentNumber == idNumber && s.IsActive &&
                    s.FirstName != null && s.LastName != null &&
                    s.FirstName.ToLower() == firstName.ToLower() &&
                    s.LastName.ToLower() == lastName.ToLower())
            : await context.Students.AsNoTracking()
                .FirstOrDefaultAsync(s => s.StudentNumber == idNumber && s.IsActive);

        if (student != null)
        {
            var result = await ProcessStudentAsync(student, campusId, scanMethod, terminalId, locationId, context);
            if (result.Success) await NotifyDashboardAsync(campusId);
            return result;
        }

        return Error($"Badge not recognized (ID:{idNumber}). Please see reception.", "NOT_FOUND");
    }

    // =================================================================
    // PARSING
    // =================================================================

    public static (string firstName, string lastName, string idNumber) ParseScanData(string rawScan)
    {
        if (string.IsNullOrWhiteSpace(rawScan)) return ("", "", "");

        var trimmed = rawScan.Trim();

        if (trimmed.Contains('|'))
        {
            var parts = trimmed.Split('|');

            if (parts.Length >= 3)
            {
                var first = parts[0].Trim();
                var last  = parts[parts.Length - 1].Trim();
                bool isReversed = first.Length > 0 && first.All(char.IsDigit)
                                && last.Length > 0 && last.All(char.IsLetter);
                if (isReversed)
                    parts = parts.Reverse()
                               .Select(p => new string(p.Trim().Reverse().ToArray()))
                               .ToArray();
                return (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());
            }

            if (parts.Length == 2)
            {
                var p0 = parts[0].Trim();
                var p1 = parts[1].Trim();
                bool isReversed = p0.All(char.IsDigit) && p1.All(char.IsLetter);
                if (isReversed)
                    return ("", "", new string(p1.Reverse().ToArray()));
                return ("", "", p1);
            }
        }

        return ("", "", trimmed);
    }

    // =================================================================
    // HOURLY EMPLOYEE PATH
    // =================================================================

    private async Task<KioskScanResult> ProcessHourlyAsync(Staff staff, TcEmployee employee,
        int campusId, string scanMethod, int terminalId, int locationId, TimeClockDbContext context)
    {
        try
        {
            var effectiveCampusId = campusId > 0 ? campusId : employee.HomeCampusId;
            var punchRequest = new PunchRequest
            {
                IdNumber = employee.IdNumber,
                CampusId = effectiveCampusId,
                ScanMethod = scanMethod,
                IsMobileMode = false,
                PunchSource = AuditSource.Kiosk
            };

            var punchResult = await _timePunchService.ProcessPunchAsync(punchRequest);
            if (!punchResult.Success)
            {
                return Error(punchResult.Message, punchResult.ErrorCode);
            }

            var scanType = DetermineScanTypeFromPunch(punchResult.PunchType);
            await RecordAttendanceTransaction(context, "STAFF", staff.IdNumber ?? employee.IdNumber,
                staff.FirstName ?? "", staff.LastName ?? "", effectiveCampusId, locationId, terminalId, scanType, scanMethod, null);

            var photo = punchResult.EmployeePhotoBase64;
            if (string.IsNullOrEmpty(photo))
            {
                var photoRecord = await context.Photos.AsNoTracking()
                    .FirstOrDefaultAsync(p => p.SubjectDcid == staff.Dcid && p.SubjectType == 1);
                photo = photoRecord?.PhotoData != null ? Convert.ToBase64String(photoRecord.PhotoData) : "";
            }

            return new KioskScanResult
            {
                Success = true,
                Message = punchResult.Message,
                PersonType = "HourlyStaff",
                PersonName = staff.FullName,
                FirstName = staff.FirstName,
                LastName = staff.LastName,
                IdNumber = staff.IdNumber,
                PhotoBase64 = photo ?? "",
                ScanType = scanType,
                ScanTypeDisplay = GetScanTypeDisplay(punchResult.PunchType),
                ScanTypeBadgeClass = GetScanTypeBadgeClass(punchResult.PunchType),
                PersonTypeDisplay = "Hourly Staff",
                TotalHoursToday = punchResult.TotalHoursToday,
                ScanTime = punchResult.PunchTime
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing hourly employee: {Id}", employee.IdNumber);
            return Error("Error processing - please try again", "EXCEPTION");
        }
    }

    // =================================================================
    // SALARIED STAFF PATH
    // =================================================================

    private async Task<KioskScanResult> ProcessStaffAsync(Staff staff, int campusId,
        string scanMethod, int terminalId, int locationId, TimeClockDbContext context)
    {
        try
        {
            var effectiveCampusId = campusId > 0 ? campusId : 1;
            var scanType = await DetermineAttendanceScanType(context, staff.IdNumber ?? "", effectiveCampusId);

            await RecordAttendanceTransaction(context, "STAFF", staff.IdNumber ?? "",
                staff.FirstName ?? "", staff.LastName ?? "", effectiveCampusId, locationId, terminalId, scanType, scanMethod, null);

            if (scanType == "CAMPUS_IN")
                await CheckAndMarkReturn(context, staff.IdNumber ?? "", effectiveCampusId);

            var photo = await context.Photos.AsNoTracking()
                .FirstOrDefaultAsync(p => p.SubjectDcid == staff.Dcid && p.SubjectType == 1);

            return new KioskScanResult
            {
                Success = true,
                Message = GetGreeting(staff.FirstName ?? "Staff", scanType == "CAMPUS_IN"),
                PersonType = "Staff",
                PersonName = staff.FullName ?? "Staff",
                FirstName = staff.FirstName,
                LastName = staff.LastName,
                IdNumber = staff.IdNumber,
                PhotoBase64 = photo?.PhotoData != null ? Convert.ToBase64String(photo.PhotoData) : "",
                ScanType = scanType,
                ScanTypeDisplay = scanType == "CAMPUS_IN" ? "CHECKED IN" : "CHECKED OUT",
                ScanTypeBadgeClass = scanType == "CAMPUS_IN" ? "in" : "out",
                PersonTypeDisplay = "Staff",
                TotalHoursToday = null,
                ScanTime = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing staff checkin: {Id}", staff.IdNumber);
            return Error("Error processing - please try again", "EXCEPTION");
        }
    }

    // =================================================================
    // STUDENT PATH
    // =================================================================

    private async Task<KioskScanResult> ProcessStudentAsync(Student student, int campusId,
        string scanMethod, int terminalId, int locationId, TimeClockDbContext context)
    {
        try
        {
            var effectiveCampusId = campusId > 0 ? campusId : 1;
            var scanType = await DetermineAttendanceScanType(context, student.StudentNumber ?? "", effectiveCampusId);

            await RecordAttendanceTransaction(context, "STUDENT", student.StudentNumber ?? "",
                student.FirstName ?? "", student.LastName ?? "", effectiveCampusId, locationId, terminalId, scanType, scanMethod, null);

            var photo = await context.Photos.AsNoTracking()
                .FirstOrDefaultAsync(p => p.SubjectDcid == student.Dcid && p.SubjectType == 0);

            return new KioskScanResult
            {
                Success = true,
                Message = GetGreeting(student.FirstName ?? "Student", scanType == "CAMPUS_IN"),
                PersonType = "Student",
                PersonName = student.FullName ?? "Student",
                FirstName = student.FirstName,
                LastName = student.LastName,
                IdNumber = student.StudentNumber,
                PhotoBase64 = photo?.PhotoData != null ? Convert.ToBase64String(photo.PhotoData) : "",
                ScanType = scanType,
                ScanTypeDisplay = scanType == "CAMPUS_IN" ? "CHECKED IN" : "CHECKED OUT",
                ScanTypeBadgeClass = scanType == "CAMPUS_IN" ? "in" : "out",
                PersonTypeDisplay = "Student",
                TotalHoursToday = null,
                ScanTime = DateTime.Now
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing student checkin: {Id}", student.StudentNumber);
            return Error("Error processing - please try again", "EXCEPTION");
        }
    }

    // =================================================================
    // SHARED HELPERS
    // =================================================================

    private async Task<string> DetermineAttendanceScanType(TimeClockDbContext context, string idNumber, int campusId)
    {
        var today = DateTime.Today;
        var lastTrans = await context.AttendanceTransactions.AsNoTracking()
            .Where(t => t.IdNumber == idNumber && t.ScanDateTime.Date == today)
            .OrderByDescending(t => t.ScanDateTime)
            .FirstOrDefaultAsync();

        if (lastTrans == null) return "CAMPUS_IN";

        return lastTrans.ScanType switch
        {
            "CAMPUS_IN" or "LUNCH_IN" => "CAMPUS_OUT",
            _ => "CAMPUS_IN"
        };
    }

    private async Task RecordAttendanceTransaction(TimeClockDbContext context, string transactionType,
        string idNumber, string firstName, string lastName, int campusId, int locationId, int terminalId,
        string scanType, string scanMethod, string? punchSubType)
    {
        var transaction = new AttendanceTransaction
        {
            TransactionType = transactionType,
            IdNumber = idNumber,
            FirstName = firstName,
            LastName = lastName,
            CampusId = campusId,
            LocationId = locationId,
            ScanDateTime = DateTime.Now,
            ScanType = scanType,
            ScanMethod = scanMethod,
            TerminalId = terminalId,
            DataSource = "LOCAL",
            ValidationStatus = "VALID",
            PunchSubType = punchSubType,
            CreatedDate = DateTime.Now
        };
        context.AttendanceTransactions.Add(transaction);
        await context.SaveChangesAsync();
    }

    private async Task CheckAndMarkReturn(TimeClockDbContext context, string idNumber, int campusId)
    {
        try
        {
            var today = DateTime.Today;
            var lastOut = await context.AttendanceTransactions.AsNoTracking()
                .Where(t => t.IdNumber == idNumber
                         && t.CampusId == campusId
                         && t.ScanDateTime.Date == today
                         && t.ScanType == "CAMPUS_OUT"
                         && t.PunchSubType != null)
                .OrderByDescending(t => t.ScanDateTime)
                .FirstOrDefaultAsync();

            if (lastOut == null) return;

            var returnTx = await context.AttendanceTransactions
                .Where(t => t.IdNumber == idNumber
                         && t.CampusId == campusId
                         && t.ScanDateTime.Date == today
                         && t.ScanType == "CAMPUS_IN")
                .OrderByDescending(t => t.ScanDateTime)
                .FirstOrDefaultAsync();

            if (returnTx != null)
            {
                returnTx.PunchSubType = $"RETURN_{lastOut.PunchSubType}";
                await context.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error marking return punch for {IdNumber}", idNumber);
        }
    }

    private async Task NotifyDashboardAsync(int campusId)
    {
        try
        {
            using var context = await _dbFactory.CreateDbContextAsync();
            var campus = await context.Campuses.AsNoTracking()
                .FirstOrDefaultAsync(c => c.CampusId == campusId);

            var campusCode = campus?.CampusCode?.ToUpper() switch
            {
                "STOPSIX" => AppConstants.Campus.StopSixCode,
                "MCCART"  => AppConstants.Campus.McCartCode,
                _         => AppConstants.Campus.DistrictCode
            };

            await _hubContext.Clients.Group($"Dashboard_{campusCode}").SendAsync("ScanCompleted");
            _logger.LogInformation("Dashboard notified for campus: {Campus}", campusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to notify dashboard for campus {CampusId}", campusId);
        }
    }

    // =================================================================
    // STATIC FORMAT HELPERS
    // =================================================================

    private static string DetermineScanTypeFromPunch(string? punchType) => punchType?.ToUpper() switch
    {
        "IN"       => "CAMPUS_IN",
        "OUT"      => "CAMPUS_OUT",
        "LUNCHOUT" => "LUNCH_OUT",
        "LUNCHIN"  => "LUNCH_IN",
        _          => "CAMPUS_IN"
    };

    private static string GetScanTypeDisplay(string? punchType) => punchType?.ToUpper() switch
    {
        "IN"       => "CLOCKED IN",
        "OUT"      => "CLOCKED OUT",
        "LUNCHOUT" => "LUNCH OUT",
        "LUNCHIN"  => "LUNCH IN",
        _          => "CHECKED IN"
    };

    private static string GetScanTypeBadgeClass(string? punchType) => punchType?.ToUpper() switch
    {
        "IN" or "LUNCHIN"   => "in",
        "OUT" or "LUNCHOUT" => "out",
        _                   => "in"
    };

    private static string GetGreeting(string firstName, bool isCheckIn)
    {
        var now = DateTime.Now;
        var greeting = now.Hour < 12 ? "morning" : now.Hour < 17 ? "afternoon" : "evening";
        return isCheckIn ? "Good " + greeting + ", " + firstName + "!" : "Goodbye, " + firstName + "!";
    }

    private static KioskScanResult Error(string message, string? code = null) => new()
    {
        Success = false,
        Message = message,
        ErrorCode = code
    };

    // 2026-07-08: Terminal resolution for the tablet kiosk route. The URL
    // /kiosk/tablet/{terminalCode} carries the bearer credential; every
    // render calls this to validate. Null return → the page renders the
    // "This terminal is offline" panel with no scan UI.
    public async Task<TcTerminal?> ResolveActiveTerminalAsync(
        string terminalCode, string? expectedDeviceType = null)
    {
        if (string.IsNullOrWhiteSpace(terminalCode)) return null;

        var codeLower = terminalCode.Trim().ToLower();
        using var context = await _dbFactory.CreateDbContextAsync();

        var terminal = await context.TcTerminals
            .AsNoTracking()
            .Include(t => t.Campus)
            .Where(t => t.IsActive
                     && t.TerminalCode.ToLower() == codeLower)
            .FirstOrDefaultAsync();

        if (terminal == null) return null;

        if (!string.IsNullOrWhiteSpace(expectedDeviceType)
            && !string.Equals(terminal.DeviceType, expectedDeviceType, StringComparison.OrdinalIgnoreCase))
        {
            // Right code, wrong hardware type. Treat as unknown to avoid
            // accidentally exposing an ESP32 to a tablet URL or vice versa.
            return null;
        }

        return terminal;
    }
}
