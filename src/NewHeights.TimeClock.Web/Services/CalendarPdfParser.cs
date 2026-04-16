using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace NewHeights.TimeClock.Web.Services;

public class CalendarPdfParser
{
    private static readonly HashSet<string> HolidayKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "Holiday", "Break", "No School", "District Closed", "School Holiday", "District Holiday"
    };

    private static readonly HashSet<string> SkipKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "First Day", "Convocation", "Orientation", "STAAR", "End of Term", "End of Year",
        "Classes", "PD", "Graduation", "Summer Teachers", "Summer Classes", "Last Day",
        "Hold for"
    };

    public record ParsedHoliday(
        string Name,
        DateOnly Date,
        decimal HoursCredited,
        bool Selected
    );

    public record ParseResult(
        string CampusName,
        string SchoolYear,
        List<ParsedHoliday> Holidays,
        List<string> Warnings
    );

    public static ParseResult ParseCalendarPdf(Stream pdfStream)
    {
        var holidays = new List<ParsedHoliday>();
        var warnings = new List<string>();
        var campusName = "";
        var schoolYear = "";

        using var document = PdfDocument.Open(pdfStream);
        var allText = string.Join("\n", document.GetPages().Select(p => p.Text));

        // Extract campus name
        if (allText.Contains("McCart", StringComparison.OrdinalIgnoreCase))
            campusName = "McCart";
        else if (allText.Contains("Stop Six", StringComparison.OrdinalIgnoreCase) ||
                 allText.Contains("StopSix", StringComparison.OrdinalIgnoreCase))
            campusName = "StopSix";

        // Extract school year (pattern: 2025-2026)
        var yearMatch = Regex.Match(allText, @"(\d{4})-(\d{4})");
        if (yearMatch.Success)
            schoolYear = yearMatch.Value;

        // Parse the Important Dates section
        // Lines look like: "Nov 24-28 Fall Break" or "Jan-19 District Holiday" or "Dec 22-Jan 7 Winter Break"
        var lines = allText.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;

            // Skip lines that aren't holiday-related
            if (SkipKeywords.Any(kw => line.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Check if this line contains a holiday keyword
            if (!HolidayKeywords.Any(kw => line.Contains(kw, StringComparison.OrdinalIgnoreCase)))
                continue;

            try
            {
                var parsed = ParseDateLine(line, schoolYear);
                if (parsed.Count > 0)
                {
                    holidays.AddRange(parsed);
                }
                else
                {
                    warnings.Add($"Could not parse dates from: \"{line}\"");
                }
            }
            catch (Exception ex)
            {
                warnings.Add($"Error parsing \"{line}\": {ex.Message}");
            }
        }

        // Deduplicate by date
        holidays = holidays
            .GroupBy(h => h.Date)
            .Select(g => g.First())
            .OrderBy(h => h.Date)
            .ToList();

        // Remove weekends
        var weekdayHolidays = holidays
            .Where(h => h.Date.DayOfWeek != DayOfWeek.Saturday && h.Date.DayOfWeek != DayOfWeek.Sunday)
            .ToList();

        var removedWeekends = holidays.Count - weekdayHolidays.Count;
        if (removedWeekends > 0)
            warnings.Add($"Removed {removedWeekends} weekend date(s).");

        return new ParseResult(campusName, schoolYear, weekdayHolidays, warnings);
    }

    private static List<ParsedHoliday> ParseDateLine(string line, string schoolYear)
    {
        var results = new List<ParsedHoliday>();

        // Determine the academic year boundaries for month->year mapping
        int startYear = 2025, endYear = 2026;
        if (!string.IsNullOrEmpty(schoolYear))
        {
            var parts = schoolYear.Split('-');
            if (parts.Length == 2 && int.TryParse(parts[0], out var sy) && int.TryParse(parts[1], out var ey))
            {
                startYear = sy;
                endYear = ey;
            }
        }

        // Extract the holiday name (everything after the date portion)
        var name = ExtractHolidayName(line);

        // Pattern 1: "Mon DD-Mon DD" range spanning months (e.g., "Dec 22-Jan 7")
        var crossMonthRange = Regex.Match(line, @"([A-Za-z]{3})\s+(\d{1,2})\s*-\s*([A-Za-z]{3})\s+(\d{1,2})");
        if (crossMonthRange.Success)
        {
            var m1 = ParseMonth(crossMonthRange.Groups[1].Value);
            var d1 = int.Parse(crossMonthRange.Groups[2].Value);
            var m2 = ParseMonth(crossMonthRange.Groups[3].Value);
            var d2 = int.Parse(crossMonthRange.Groups[4].Value);

            if (m1 > 0 && m2 > 0)
            {
                var y1 = MonthToYear(m1, startYear, endYear);
                var y2 = MonthToYear(m2, startYear, endYear);
                var start = new DateOnly(y1, m1, d1);
                var end = new DateOnly(y2, m2, d2);

                for (var d = start; d <= end; d = d.AddDays(1))
                {
                    results.Add(new ParsedHoliday(name, d, 8.00m, true));
                }
                return results;
            }
        }

        // Pattern 2: "Mon DD-DD" same-month range (e.g., "Nov 24-28")
        var sameMonthRange = Regex.Match(line, @"([A-Za-z]{3})\s+(\d{1,2})\s*-\s*(\d{1,2})");
        if (sameMonthRange.Success)
        {
            var m = ParseMonth(sameMonthRange.Groups[1].Value);
            var d1 = int.Parse(sameMonthRange.Groups[2].Value);
            var d2 = int.Parse(sameMonthRange.Groups[3].Value);

            if (m > 0)
            {
                var y = MonthToYear(m, startYear, endYear);
                for (int day = d1; day <= d2; day++)
                {
                    results.Add(new ParsedHoliday(name, new DateOnly(y, m, day), 8.00m, true));
                }
                return results;
            }
        }

        // Pattern 3: "Mon-DD" single date (e.g., "Jan-19" or "Apr-3")
        var singleDate = Regex.Match(line, @"([A-Za-z]{3})-(\d{1,2})");
        if (singleDate.Success)
        {
            var m = ParseMonth(singleDate.Groups[1].Value);
            var d = int.Parse(singleDate.Groups[2].Value);

            if (m > 0)
            {
                var y = MonthToYear(m, startYear, endYear);
                results.Add(new ParsedHoliday(name, new DateOnly(y, m, d), 8.00m, true));
                return results;
            }
        }

        // Pattern 4: "Mon DD-Mon DD" with full month names
        var fullMonthRange = Regex.Match(line, @"([A-Za-z]+)\s+(\d{1,2})\s*-\s*([A-Za-z]+)\s+(\d{1,2})");
        if (fullMonthRange.Success)
        {
            var m1 = ParseMonth(fullMonthRange.Groups[1].Value);
            var d1 = int.Parse(fullMonthRange.Groups[2].Value);
            var m2 = ParseMonth(fullMonthRange.Groups[3].Value);
            var d2 = int.Parse(fullMonthRange.Groups[4].Value);

            if (m1 > 0 && m2 > 0)
            {
                var y1 = MonthToYear(m1, startYear, endYear);
                var y2 = MonthToYear(m2, startYear, endYear);
                var start = new DateOnly(y1, m1, d1);
                var end = new DateOnly(y2, m2, d2);

                for (var dt = start; dt <= end; dt = dt.AddDays(1))
                {
                    results.Add(new ParsedHoliday(name, dt, 8.00m, true));
                }
                return results;
            }
        }

        return results;
    }

    private static string ExtractHolidayName(string line)
    {
        // Remove the date portion and return the rest as the name
        var cleaned = Regex.Replace(line, @"^[A-Za-z]{3}\s*\d{1,2}\s*-\s*[A-Za-z]{0,3}\s*\d{0,2}\s*", "").Trim();
        cleaned = Regex.Replace(cleaned, @"^[A-Za-z]{3}-\d{1,2}\s*", "").Trim();

        if (string.IsNullOrWhiteSpace(cleaned))
            return "Holiday";

        return cleaned;
    }

    private static int ParseMonth(string monthStr)
    {
        var abbrevs = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["Jan"] = 1, ["Feb"] = 2, ["Mar"] = 3, ["Apr"] = 4,
            ["May"] = 5, ["Jun"] = 6, ["Jul"] = 7, ["Aug"] = 8,
            ["Sep"] = 9, ["Oct"] = 10, ["Nov"] = 11, ["Dec"] = 12,
            ["January"] = 1, ["February"] = 2, ["March"] = 3, ["April"] = 4,
            ["June"] = 6, ["July"] = 7, ["August"] = 8,
            ["September"] = 9, ["October"] = 10, ["November"] = 11, ["December"] = 12
        };

        return abbrevs.TryGetValue(monthStr, out var month) ? month : 0;
    }

    private static int MonthToYear(int month, int startYear, int endYear)
    {
        // Academic year: Jul-Dec = startYear, Jan-Jun = endYear
        return month >= 7 ? startYear : endYear;
    }
}
