using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NewHeights.TimeClock.Data;
using NewHeights.TimeClock.Data.Entities;
using NewHeights.TimeClock.Shared.Enums;

namespace NewHeights.TimeClock.Web.Services;

public interface IGeofenceService
{
    Task<GeofenceResult> ValidateLocationAsync(int campusId, double latitude, double longitude);
    Task<GeofenceResult> ValidateLocationAsync(string campusCode, double latitude, double longitude);
    Task<Campus?> GetNearestCampusAsync(double latitude, double longitude);
    Task<List<Campus>> GetAllCampusesAsync();
    double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2);
}

public class GeofenceResult
{
    public bool IsValid { get; set; }
    public GeofenceStatus Status { get; set; }
    public double DistanceMeters { get; set; }
    public int? CampusId { get; set; }
    public string? CampusName { get; set; }
    public string? Message { get; set; }
}

public class GeofenceService : IGeofenceService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<GeofenceService> _logger;
    
    private static List<Campus>? _campusCache;
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly SemaphoreSlim _cacheLock = new(1, 1);

    public GeofenceService(IServiceScopeFactory scopeFactory, ILogger<GeofenceService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    private async Task<List<Campus>> GetCampusesCachedAsync()
    {
        if (_campusCache != null && DateTime.Now < _cacheExpiry)
        {
            return _campusCache;
        }

        await _cacheLock.WaitAsync();
        try
        {
            if (_campusCache != null && DateTime.Now < _cacheExpiry)
            {
                return _campusCache;
            }

            using var scope = _scopeFactory.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<TimeClockDbContext>();
            
            var campuses = await context.Campuses
                .AsNoTracking()
                .Where(c => c.Latitude.HasValue && c.Longitude.HasValue)
                .Select(c => new Campus
                {
                    CampusId = c.CampusId,
                    CampusCode = c.CampusCode,
                    CampusName = c.CampusName,
                    Latitude = c.Latitude,
                    Longitude = c.Longitude,
                    GeofenceRadiusMeters = c.GeofenceRadiusMeters,
                    CampusWifiSSID = c.CampusWifiSSID,
                    GeofencePolygon = c.GeofencePolygon
                })
                .ToListAsync();

            _campusCache = campuses;
            _cacheExpiry = DateTime.Now.AddMinutes(30);
            _logger.LogInformation("Campus cache loaded: {Count} campuses", campuses.Count);
            return campuses;
        }
        finally
        {
            _cacheLock.Release();
        }
    }

    public async Task<List<Campus>> GetAllCampusesAsync()
    {
        return await GetCampusesCachedAsync();
    }

    public async Task<GeofenceResult> ValidateLocationAsync(int campusId, double latitude, double longitude)
    {
        var campuses = await GetCampusesCachedAsync();
        var campus = campuses.FirstOrDefault(c => c.CampusId == campusId);
        
        if (campus == null)
            return new GeofenceResult { IsValid = false, Status = GeofenceStatus.OutOfRange, Message = "Campus not found" };

        return ValidateAgainstCampus(campus, latitude, longitude);
    }

    public async Task<GeofenceResult> ValidateLocationAsync(string campusCode, double latitude, double longitude)
    {
        var campuses = await GetCampusesCachedAsync();
        var campus = campuses.FirstOrDefault(c => c.CampusCode.Equals(campusCode, StringComparison.OrdinalIgnoreCase));
        
        if (campus == null)
            return new GeofenceResult { IsValid = false, Status = GeofenceStatus.OutOfRange, Message = $"Campus '{campusCode}' not found" };

        return ValidateAgainstCampus(campus, latitude, longitude);
    }

    public async Task<Campus?> GetNearestCampusAsync(double latitude, double longitude)
    {
        var campuses = await GetCampusesCachedAsync();
        if (!campuses.Any()) return null;

        Campus? nearest = null;
        double minDistance = double.MaxValue;

        foreach (var campus in campuses)
        {
            var distance = CalculateDistanceMeters(latitude, longitude, (double)campus.Latitude!.Value, (double)campus.Longitude!.Value);
            if (distance < minDistance)
            {
                minDistance = distance;
                nearest = campus;
            }
        }

        return nearest;
    }

    private GeofenceResult ValidateAgainstCampus(Campus campus, double latitude, double longitude)
    {
        // Distance to the campus center is always computed — used in the user-
        // facing message even when the polygon is the authoritative check.
        var distance = CalculateDistanceMeters(
            latitude, longitude,
            (double)campus.Latitude!.Value,
            (double)campus.Longitude!.Value);

        // Polygon takes precedence when present. Falls back to the circular
        // radius check when the polygon is missing or malformed.
        var polygon = ParsePolygonVertices(campus.GeofencePolygon);
        bool isValid;
        string method;
        if (polygon != null && polygon.Count >= 3)
        {
            isValid = IsPointInPolygon(latitude, longitude, polygon);
            method = "polygon";
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(campus.GeofencePolygon))
            {
                _logger.LogWarning(
                    "Geofence: campus {Code} has GeofencePolygon but it's malformed or has <3 vertices \u2014 falling back to radius.",
                    campus.CampusCode);
            }
            isValid = distance <= campus.GeofenceRadiusMeters;
            method = "radius";
        }

        return new GeofenceResult
        {
            IsValid = isValid,
            Status = isValid ? GeofenceStatus.Verified : GeofenceStatus.OutOfRange,
            DistanceMeters = distance,
            CampusId = campus.CampusId,
            CampusName = campus.CampusName,
            Message = isValid
                ? $"Within {campus.CampusName} ({method})"
                : $"Outside {campus.CampusName} \u2014 {distance:F0}m from center ({method})"
        };
    }

    /// <summary>
    /// Parses GeoJSON into a list of (Lat, Lon) vertices for the outer ring.
    /// Accepts three common shapes emitted by geojson.io and similar tools:
    ///   1. Raw Polygon object:  {"type":"Polygon","coordinates":[[[lon,lat],...]]}
    ///   2. Feature wrapper:     {"type":"Feature","geometry":{...polygon...}}
    ///   3. FeatureCollection:   {"type":"FeatureCollection","features":[...]}
    /// Returns null on parse failure or missing ring. GeoJSON uses
    /// [longitude, latitude] order — we flip to (Lat, Lon) for consistency
    /// with the rest of the codebase.
    /// </summary>
    private List<(double Lat, double Lon)>? ParsePolygonVertices(string? geoJson)
    {
        if (string.IsNullOrWhiteSpace(geoJson)) return null;

        try
        {
            using var doc = JsonDocument.Parse(geoJson);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("type", out var typeElem)) return null;

            var type = typeElem.GetString();
            JsonElement polygon;

            if (string.Equals(type, "FeatureCollection", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("features", out var features)
                 || features.ValueKind != JsonValueKind.Array
                 || features.GetArrayLength() == 0)
                    return null;
                if (!features[0].TryGetProperty("geometry", out polygon)) return null;
            }
            else if (string.Equals(type, "Feature", StringComparison.OrdinalIgnoreCase))
            {
                if (!root.TryGetProperty("geometry", out polygon)) return null;
            }
            else
            {
                polygon = root;
            }

            if (!polygon.TryGetProperty("coordinates", out var coords)
             || coords.ValueKind != JsonValueKind.Array
             || coords.GetArrayLength() == 0)
                return null;

            // First ring is the outer boundary; holes (additional rings) ignored.
            var ring = coords[0];
            if (ring.ValueKind != JsonValueKind.Array) return null;

            var result = new List<(double Lat, double Lon)>(ring.GetArrayLength());
            foreach (var pt in ring.EnumerateArray())
            {
                if (pt.ValueKind != JsonValueKind.Array || pt.GetArrayLength() < 2) continue;
                // GeoJSON: [longitude, latitude]
                var lon = pt[0].GetDouble();
                var lat = pt[1].GetDouble();
                result.Add((lat, lon));
            }
            return result.Count >= 3 ? result : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Geofence: failed to parse GeofencePolygon GeoJSON.");
            return null;
        }
    }

    /// <summary>
    /// Ray-casting point-in-polygon. Treats lat/lon as planar coordinates —
    /// accurate at the scale of a school campus (&lt;1km). For larger polygons
    /// a spherical calculation would be needed.
    /// </summary>
    private static bool IsPointInPolygon(double testLat, double testLon, List<(double Lat, double Lon)> ring)
    {
        var inside = false;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
        {
            var (lat_i, lon_i) = ring[i];
            var (lat_j, lon_j) = ring[j];

            var latCrosses = (lat_i > testLat) != (lat_j > testLat);
            if (!latCrosses) continue;

            // Longitude of the edge at the test point's latitude.
            var lonAtTestLat = lon_i + (testLat - lat_i) * (lon_j - lon_i) / (lat_j - lat_i);
            if (testLon < lonAtTestLat) inside = !inside;
        }
        return inside;
    }

    public double CalculateDistanceMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371000;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}