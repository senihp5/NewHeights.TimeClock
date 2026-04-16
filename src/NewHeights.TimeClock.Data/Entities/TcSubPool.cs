namespace NewHeights.TimeClock.Data.Entities;

public class TcSubPool
{
    public int SubPoolId { get; set; }
    public int? EmployeeId { get; set; }
    public string? ExternalName { get; set; }
    public string? ExternalEmail { get; set; }
    public string? ExternalPhone { get; set; }
    public string? CertificationType { get; set; }
    public string? QualifiedSubjects { get; set; }
    public string? QualifiedGradeLevels { get; set; }
    public string? AvailableCampuses { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedDate { get; set; } = DateTime.Now;

    public TcEmployee? Employee { get; set; }
    public ICollection<TcSubRequest> Assignments { get; set; } = new List<TcSubRequest>();
}
