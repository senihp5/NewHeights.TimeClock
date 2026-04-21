-- =============================================================================
-- Migration 040: Polygonal geofence option on Attendance_Campuses
-- Date: 2026-04-21 (geofence tightening)
-- Idempotent — safe to re-run.
--
-- Adds an optional GeofencePolygon column (GeoJSON string) to each campus.
-- When populated, GeofenceService uses point-in-polygon validation instead of
-- the circular Latitude + Longitude + GeofenceRadiusMeters check. When NULL,
-- the existing circular check still applies — rollout is per-campus, no
-- big-bang migration required.
--
-- Storage is NVARCHAR(MAX) holding GeoJSON. Accepted shapes (parser handles
-- all three):
--   1. Raw Polygon:        {"type":"Polygon","coordinates":[[[lon,lat],...]]}
--   2. Feature wrapper:    {"type":"Feature","geometry":{...polygon...}}
--   3. FeatureCollection:  {"type":"FeatureCollection","features":[...]}
--
-- The GeoJSON spec uses [longitude, latitude] order (yes, flipped). The
-- ExtractRing helper in GeofenceService handles that convention.
--
-- Requires that Latitude/Longitude are still populated (used by
-- GetNearestCampusAsync before polygon validation kicks in).
-- =============================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Attendance_Campuses')
      AND name = 'GeofencePolygon'
)
BEGIN
    ALTER TABLE dbo.Attendance_Campuses
        ADD GeofencePolygon NVARCHAR(MAX) NULL;
END
GO
