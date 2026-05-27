namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Top-level multi-tenant boundary. Every Campus, ClassSection,
/// ClassAttendance, and ClassAttendanceSheet row carries a DistrictId
/// so future expansion to additional school districts does not require
/// a schema migration.
///
/// At launch only New Heights Adult High School exists (DistrictId = 1,
/// seeded by migration 062).
/// </summary>
public class District
{
    public int DistrictId { get; set; }
    public required string DistrictCode { get; set; }
    public required string DistrictName { get; set; }

    /// <summary>
    /// Display name resolved via TimeZoneInfo.FindSystemTimeZoneById.
    /// Defaults to "Central Standard Time" for NH.
    /// </summary>
    public string TimeZone { get; set; } = "Central Standard Time";

    /// <summary>
    /// Configuration key used by IClassRosterService to resolve the CMS
    /// (CaseManagementDB) connection string. NH uses "DefaultConnection"
    /// because CMS lives on the same Azure SQL server as TimeClock; a
    /// future district can point at its own CMS instance by adding a
    /// distinct connection string in App Service config and updating
    /// this column.
    /// </summary>
    public string CmsConnectionStringName { get; set; } = "DefaultConnection";

    /// <summary>
    /// PowerSchool base URL for the district. Used by class attendance
    /// archive PDFs and any future API-mediated PS interactions.
    /// </summary>
    public string? PowerSchoolBaseUrl { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? ModifiedDate { get; set; }

    // Navigation
    public ICollection<Campus> Campuses { get; set; } = new List<Campus>();
}
