-- Migration 032: Add DisplayName column to TC_Employees
-- Date: 2026-04-16
-- Purpose: Store Entra display name directly on employee record.
--          Substitutes and other employees without PowerSchool Staff records
--          need a name source for payroll, timesheets, and admin views.

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TC_Employees') AND name = 'DisplayName')
BEGIN
    ALTER TABLE TC_Employees ADD DisplayName NVARCHAR(200) NULL;
    PRINT 'Migration 032: Added DisplayName column to TC_Employees.';
END
ELSE
BEGIN
    PRINT 'Migration 032: DisplayName column already exists. Skipped.';
END
