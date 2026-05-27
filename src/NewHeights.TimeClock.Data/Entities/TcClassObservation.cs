using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Per-student teacher journal entry. Internal-only - never
/// transcribed to PowerSchool. Multiple observations per
/// (student, section, date) are allowed and expected: each one
/// represents a single observed moment, with its own timestamp
/// and author.
///
/// Soft-delete via IsActive so an entry made in error can be
/// redacted from current views without losing the audit trail.
/// </summary>
public class TcClassObservation
{
    public long ObservationId { get; set; }

    public int ClassSectionId { get; set; }
    public int StudentDcid { get; set; }
    public required string StudentNumber { get; set; }
    public string? StudentLastName { get; set; }
    public string? StudentFirstName { get; set; }

    /// <summary>The school day this observation pertains to.</summary>
    public DateOnly ObservationDate { get; set; }

    /// <summary>
    /// When the teacher actually wrote the observation. Usually within
    /// minutes of the observed event; can be back-dated to ObservationDate
    /// if the teacher journals at end of day.
    /// </summary>
    public DateTime ObservationDateTime { get; set; } = DateTime.Now;

    public int DistrictId { get; set; } = 1;
    public int CampusId { get; set; }

    public ClassObservationCategory Category { get; set; } = ClassObservationCategory.Other;

    public required string ObservationText { get; set; }

    public required string AuthorEmail { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedDate { get; set; } = DateTime.Now;
    public DateTime? ModifiedDate { get; set; }

    // Navigation
    public TcClassSection? ClassSection { get; set; }
    public Student? Student { get; set; }
    public District? District { get; set; }
    public Campus? Campus { get; set; }
}
