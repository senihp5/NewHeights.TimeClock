namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Maps raw master-schedule course tokens (e.g. "PROF COMM", "ALG 1B") to
/// short department tags (LAUNCH, ENG, MATH, etc.) used on
/// TcSubstitutePeriodEntry.ContentArea and the teacher-level
/// TcMasterSchedule.ContentArea fallback.
///
/// LAUNCH and EXP CERT are New Heights-specific programs (financial literacy /
/// career readiness and Expanded Certifications, respectively) and must be
/// checked BEFORE the core-academic rules so "PROF COMM" doesn't leak into
/// ENG via a generic "contains COMM" match.
/// </summary>
public static class CourseTagMapper
{
    public static string? MapCourseToContentArea(string? course)
    {
        if (string.IsNullOrWhiteSpace(course)) return null;
        var c = course.ToUpperInvariant().Trim();
        if (c == "PLAN") return null;

        if (c.Contains("PROF COMM")
         || c.Contains("MONEY MATTERS")
         || c.Contains("$ MATTERS")
         || c.Contains("PERSONAL FINANCE")
         || c == "PFL" || c.StartsWith("PFL ") || c.EndsWith(" PFL")
         || c.Contains("CAREER PREP")) return "LAUNCH";

        if (c.Contains("PRACTICUM")) return "EXP CERT";

        if (c.StartsWith("ENG ") || c.StartsWith("ENG1") || c.StartsWith("ENG2")
         || c.StartsWith("ENG3") || c.StartsWith("ENG4")
         || c.Contains("E2 BOOT") || c.Contains("ENG 2 BOOT")) return "ENG";

        if (c.Contains("ALG") || c.Contains("GEOM") || c.Contains("MATH MOD")
         || c.Contains("NL MATH") || c.Contains("PRE ALG")
         || c.Contains("A1 BOOT") || c.Contains("ALGEBRA BOOT")) return "MATH";

        if (c.Contains("BIO") || c.Contains("CHEM")
         || c == "IPC" || c.StartsWith("IPC ") || c.StartsWith("IPC/")) return "SCI";

        if (c.Contains("HIST") || c.Contains("USH") || c.Contains("GEOG")
         || c.Contains("GOV") || c.Contains("ECON")) return "SS";

        if (c.Contains("SPAN") || c.Contains("FREN") || c.Contains("ESL")) return "FL";

        if (c.StartsWith("ART ") || c.StartsWith("ART1") || c.StartsWith("ART 1")
         || c.Contains("ART CURR") || c.Contains("BAND") || c.Contains("MUSIC")) return "ART";

        if (c == "PE" || c.StartsWith("PE ")) return "PE";

        if (c.Contains("KHAN LAB") || c.Contains("BEABLE")) return "LAB";

        return null;
    }

    /// <summary>
    /// Teacher-level tag list for TcMasterSchedule.ContentArea — distinct comma-
    /// joined tags from all 12 period cells, truncated to 20 chars to fit the
    /// column width. A teacher with PROF COMM + ENG 4B ends up as "LAUNCH, ENG".
    /// Not used for per-period derivation (per-period uses MapCourseToContentArea
    /// directly); this is for SQL diagnostics and as a fallback when a per-period
    /// course lookup fails.
    /// </summary>
    public static string? DeriveForTeacherRow(params string?[] periodCells)
    {
        var distinct = periodCells
            .Select(MapCourseToContentArea)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinct.Count == 0) return null;
        var joined = string.Join(", ", distinct);
        return joined.Length <= 20 ? joined : joined.Substring(0, 20);
    }
}
