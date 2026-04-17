namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Maps to existing Students table in IDSuite3 database (READ-ONLY)
/// </summary>
public class Student
{
    public int Dcid { get; set; }
    public int Id { get; set; }
    public string? StudentNumber { get; set; }  // Maps to IdNumber column
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? MiddleName { get; set; }
    public string? Grade { get; set; }
    public string? SchoolName { get; set; }
    public bool IsActive { get; set; }
    public DateTime LastModified { get; set; }

    /// <summary>
    /// Student's @newheightshs.com Google Workspace email. Used by Phase 8 self
    /// check-in (/student/checkin) to map a Google OAuth login to the Student row.
    /// Read-only like the rest of this entity — source of truth is the IDSuite3
    /// Students table.
    /// </summary>
    public string? Email { get; set; }

    public string FullName => $"{FirstName} {LastName}".Trim();
}