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
                    CampusWifiSSID = c.CampusWifiSSID
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
        var distance = CalculateDistanceMeters(latitude, longitude, (double)campus.Latitude!.Value, (double)campus.Longitude!.Value);
        var isWithinRadius = distance <= campus.GeofenceRadiusMeters;

        return new GeofenceResult
        {
            IsValid = isWithinRadius,
            Status = isWithinRadius ? GeofenceStatus.Verified : GeofenceStatus.OutOfRange,
            DistanceMeters = distance,
            CampusId = campus.CampusId,
            CampusName = campus.CampusName,
            Message = isWithinRadius ? $"Within {campus.CampusName}" : $"Too far from {campus.CampusName} ({distance:F0}m away)"
        };
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