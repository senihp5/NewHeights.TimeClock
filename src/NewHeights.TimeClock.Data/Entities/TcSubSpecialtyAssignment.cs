namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Migration 045 / Phase D5: many-to-many between admin subs and the
/// TcSubSpecialty catalog. Unique on (EmployeeId, SpecialtyId) so the same
/// (sub, specialty) pair can't double-assign.
/// </summary>
public class TcSubSpecialtyAssignment
{
    public long AssignmentId { get; set; }
    public int EmployeeId { get; set; }
    public int SpecialtyId { get; set; }
    public string? AssignedBy { get; set; }
    public DateTime AssignedDate { get; set; } = DateTime.Now;

    public TcEmployee Employee { get; set; } = null!;
    public TcSubSpecialty Specialty { get; set; } = null!;
}
