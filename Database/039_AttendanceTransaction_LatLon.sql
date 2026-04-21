-- =============================================================================
-- Migration 039: Persist GPS coordinates on Attendance_Transactions
-- Date: 2026-04-21 (geofence tightening)
-- Idempotent — safe to re-run.
--
-- StudentCheckin, MobileCheckin, and any future location-validated path
-- resolves browser GPS and runs GeofenceService.ValidateLocationAsync before
-- creating the transaction. Up to this point the coords were validated but
-- never persisted. This migration adds the columns so the audit trail shows
-- the GPS proof of every location-gated punch.
--
-- Matches TcTimePunch precision: decimal(9,6) for coords (~11cm resolution,
-- overkill for 150m geofence but standard). Accuracy is browser-reported
-- radius-of-confidence in meters.
-- =============================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Attendance_Transactions')
      AND name = 'Latitude'
)
BEGIN
    ALTER TABLE dbo.Attendance_Transactions
        ADD Latitude DECIMAL(9, 6) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Attendance_Transactions')
      AND name = 'Longitude'
)
BEGIN
    ALTER TABLE dbo.Attendance_Transactions
        ADD Longitude DECIMAL(9, 6) NULL;
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Attendance_Transactions')
      AND name = 'LocationAccuracyMeters'
)
BEGIN
    ALTER TABLE dbo.Attendance_Transactions
        ADD LocationAccuracyMeters DECIMAL(8, 2) NULL;
END
GO
