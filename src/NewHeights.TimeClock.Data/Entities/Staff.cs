namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Maps to existing Staff table in IDSuite3 database (READ-ONLY)
/// </summary>
public class Staff
{
    public int Dcid { get; set; }
    public int? Id { get; set; }
    public string? IdNumber { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? JobTitle { get; set; }
    public string? SchoolName { get; set; }
    public bool IsActive { get; set; }

    /// <summary>
    /// PowerSchool school ID (e.g. 220822001 for Stop Six, 220822002 for McCart).
    /// Used to scope teacher matching in Schedule Import — multi-campus teachers
    /// (Jose Lagunas, for example) have a separate Staff row per campus, and
    /// the importer needs to prefer the row whose SchoolId matches the campus
    /// being imported. Joins to Campus.PowerSchoolId.
    ///
    /// Typed as long? because the Staff.SchoolId column is bigint. Comparisons
    /// against Campus.PowerSchoolId (int?) widen automatically at the call site.
    /// </summary>
    public long? SchoolId { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}
