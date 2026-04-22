-- =============================================================================
-- Migration 041: GPS waypoint capture table for geofence tuning
-- Date: 2026-04-21 (geofence tightening)
-- Idempotent — safe to re-run.
--
-- Created to solve a real tuning problem: after migration 040 added polygon
-- geofencing, Patrick reported the fence is too tight — points well inside
-- the campus are reporting as outside. This table backs the /admin/gps-test
-- page: walk the perimeter with a phone, capture coordinates at known
-- landmarks (corners of buildings, edges of parking lots, etc.), and use the
-- saved points to build an accurate GeoJSON polygon for Attendance_Campuses.
--
-- WasInsideGeofence + GeofenceMethod record whether the current geofence
-- said "inside" at capture time, so you can see exactly which real-world
-- points the fence is getting wrong.
--
-- Coord precision matches Attendance_Transactions / Attendance_Campuses:
-- decimal(9,6) — ~11cm resolution. AccuracyMeters is the browser-reported
-- radius-of-confidence (typically 5-30m on a phone outdoors).
-- =============================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.tables
    WHERE name = 'Attendance_GeofenceTestPoints'
      AND schema_id = SCHEMA_ID('dbo')
)
BEGIN
    CREATE TABLE dbo.Attendance_GeofenceTestPoints (
        TestPointId         INT             IDENTITY(1,1) NOT NULL,
        CampusId            INT             NOT NULL,
        Label               NVARCHAR(100)   NOT NULL,
        Latitude            DECIMAL(9, 6)   NOT NULL,
        Longitude           DECIMAL(9, 6)   NOT NULL,
        AccuracyMeters      DECIMAL(8, 2)   NULL,
        Notes               NVARCHAR(500)   NULL,
        WasInsideGeofence   BIT             NULL,
        GeofenceMethod      NVARCHAR(20)    NULL,
        CapturedAtUtc       DATETIME2(3)    NOT NULL
            CONSTRAINT DF_GeofenceTestPoints_CapturedAtUtc DEFAULT SYSUTCDATETIME(),
        CapturedBy          NVARCHAR(200)   NULL,
        CONSTRAINT PK_GeofenceTestPoints
            PRIMARY KEY CLUSTERED (TestPointId),
        CONSTRAINT FK_GeofenceTestPoints_Campus
            FOREIGN KEY (CampusId) REFERENCES dbo.Attendance_Campuses(CampusId)
    );
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_GeofenceTestPoints_Campus_CapturedAt'
      AND object_id = OBJECT_ID('dbo.Attendance_GeofenceTestPoints')
)
BEGIN
    CREATE INDEX IX_GeofenceTestPoints_Campus_CapturedAt
        ON dbo.Attendance_GeofenceTestPoints(CampusId, CapturedAtUtc DESC);
END
GO
