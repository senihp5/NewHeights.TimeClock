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
    
    public string FullName => $"{FirstName} {LastName}".Trim();
}
