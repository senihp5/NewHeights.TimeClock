-- ============================================================
-- Migration 003: Add PunchSubType to Attendance_Transactions
-- Purpose: Stores early-out reason on staff Out punches
--          and RETURN_* marker on same-day return In punches
-- Values:  LUNCH | MEDICAL | MEETING | PERSONAL | EMERGENCY
--          RETURN_LUNCH | RETURN_MEDICAL | etc.
-- Run:     SSMS against IDCardPrinterDB (or IDSuite3 DB)
-- Safe:    Idempotent - checks column existence before altering
-- ============================================================

IF NOT EXISTS (
    SELECT 1
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME   = 'Attendance_Transactions'
      AND COLUMN_NAME  = 'PunchSubType'
)
BEGIN
    ALTER TABLE Attendance_Transactions
    ADD PunchSubType NVARCHAR(50) NULL;

    PRINT 'Column PunchSubType added to Attendance_Transactions.';
END
ELSE
BEGIN
    PRINT 'Column PunchSubType already exists - skipped.';
END
GO
