namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// Maps to existing Photos table in IDSuite3 database (READ-ONLY)
/// </summary>
public class Photo
{
    public int SubjectDcid { get; set; }
    public byte SubjectType { get; set; }
    public byte[]? PhotoData { get; set; }
}
