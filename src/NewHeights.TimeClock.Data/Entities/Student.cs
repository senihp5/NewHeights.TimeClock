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

    public string FullName => $"{FirstName} {LastName}".Trim();
}