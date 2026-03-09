using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Shared.DTOs;

public class EmployeeDto
{
    public int EmployeeId { get; set; }
    public int StaffDcid { get; set; }
    public required string IdNumber { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public string FullName => $"{FirstName} {LastName}";
    public string? JobTitle { get; set; }
    public EmployeeType EmployeeType { get; set; }
    public required string HomeCampusCode { get; set; }
    public string? DepartmentName { get; set; }
    public string? PhotoBase64 { get; set; }
    public bool IsActive { get; set; }
}
