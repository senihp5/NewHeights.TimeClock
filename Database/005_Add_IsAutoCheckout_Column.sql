-- Migration: Add IsAutoCheckout column to TC_TimePunches
-- Created: 2026-03-31
-- Purpose: Track automatic 9:30 PM checkouts for compliance monitoring

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TC_TimePunches') AND name = 'IsAutoCheckout')
BEGIN
    ALTER TABLE TC_TimePunches
    ADD IsAutoCheckout BIT NOT NULL DEFAULT 0;
    
    PRINT 'Added IsAutoCheckout column to TC_TimePunches';
END
ELSE
BEGIN
    PRINT 'IsAutoCheckout column already exists';
END
GO



SELECT CampusId, CampusCode, CampusName FROM Attendance_Campuses ORDER BY CampusId;