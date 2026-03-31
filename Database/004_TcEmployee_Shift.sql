-- ============================================================
-- Migration 004: Add Shift column to TC_Employees
-- Purpose: Tracks which session(s) an employee works —
--          Day, Evening, or Both.
--          Used to match the correct TC_StaffHoursWindow when
--          evaluating early-out prompts and timecard validation.
--          Stored as NVARCHAR to match EmployeeType pattern.
--
-- Mapping from HR job title (set by EmployeeSyncService):
--   EVENING * TEACHER          -> 'Evening'
--   SUBSTITUTE- EVENING only   -> 'Evening'
--   SUBSTITUTE- DAYTIME only   -> 'Day'
--   SUBSTITUTE- DAYTIME+EVE    -> 'Both'
--   All other staff/teachers   -> 'Day' (default)
--
-- Run: SSMS against IDCardPrinterDB (or IDSuite3 DB)
-- Safe: Idempotent — checks column existence before altering
-- ============================================================

IF NOT EXISTS (
    SELECT 1
    FROM INFORMATION_SCHEMA.COLUMNS
    WHERE TABLE_NAME  = 'TC_Employees'
      AND COLUMN_NAME = 'Shift'
)
BEGIN
    ALTER TABLE TC_Employees
    ADD Shift NVARCHAR(10) NOT NULL CONSTRAINT DF_TC_Employees_Shift DEFAULT 'Day';

    PRINT 'Column Shift added to TC_Employees (default Day).';
END
ELSE
BEGIN
    PRINT 'Column Shift already exists — skipped.';
END
GO

-- Verify
SELECT COLUMN_NAME, DATA_TYPE, CHARACTER_MAXIMUM_LENGTH, COLUMN_DEFAULT, IS_NULLABLE
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME  = 'TC_Employees'
  AND  COLUMN_NAME = 'Shift';
GO
