namespace NewHeights.TimeClock.Data.Entities;

/// <summary>
/// GPS waypoint captured during geofence tuning via /admin/gps-test.
/// Maps to dbo.Attendance_GeofenceTestPoints (migration 041).
/// Used to walk a campus perimeter, save landmark coordinates, and
/// later build an accurate GeoJSON polygon for Attendance_Campuses.GeofencePolygon.
/// </summary>
public class GeofenceTestPoint
{
    public int TestPointId { get; set; }
    public int CampusId { get; set; }
    public required string Label { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }

    /// <summary>Browser-reported GPS accuracy in meters (radius of confidence).</summary>
    public decimal? AccuracyMeters { get; set; }

    public string? Notes { get; set; }

    /// <summary>
    /// What the active geofence said about this point at capture time —
    /// true if it was considered inside, false if outside. Lets you see
    /// exactly which real-world points the fence is getting wrong.
    /// </summary>
    public bool? WasInsideGeofence { get; set; }

    /// <summary>"polygon" or "radius" — which check produced WasInsideGeofence.</summary>
    public string? GeofenceMethod { get; set; }

    public DateTime CapturedAtUtc { get; set; }
    public string? CapturedBy { get; set; }

    public Campus? Campus { get; set; }
}
