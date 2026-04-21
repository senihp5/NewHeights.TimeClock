namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Migration 045 / Phase D5: catalog of specialties that can be assigned to
/// admin subs (SubRole = RECEPTION only — teacher subs don't carry specialties
/// in this phase). Seed row is "Admissions"; HR adds more via the admin page
/// as new specialties emerge.
/// </summary>
public class TcSubSpecialty
{
    public int SpecialtyId { get; set; }
    public string SpecialtyName { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; } = 100;
    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime ModifiedDate { get; set; } = DateTime.Now;

    public ICollection<TcSubSpecialtyAssignment> Assignments { get; set; }
        = new List<TcSubSpecialtyAssignment>();
}
