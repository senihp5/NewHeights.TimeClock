/*
===============================================================================
Migration 051 — Add TeacherReplacedEmployeeId column to TC_SubstitutePeriodEntries
Date: 2026-04-22
Purpose:
  Persist the TC_Employees.EmployeeId of the teacher a sub is replacing, in
  addition to the existing denormalized text snapshot TeacherReplaced. The ID
  column enables HR to match sub period entries against Ascender PTO requests
  by payroll employee number rather than fuzzy name string.

Column properties:
  - INT NULL — nullable because:
      (a) Walk-in coverage with free-text teacher name (Task C) has no ID;
      (b) Teachers not yet synced into TC_Employees won't resolve;
      (c) Legacy rows imported before this migration.
  - FK constraint to TC_Employees(EmployeeId) with ON DELETE SET NULL so
    deactivating/removing a teacher's employee record keeps the audit trail
    on existing sub entries without cascade-deleting them.

Idempotent: safe to run multiple times.
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 051: TeacherReplacedEmployeeId';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';
GO

-------------------------------------------------------------------------------
-- 1. Add the column
-------------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_SubstitutePeriodEntries')
      AND name = 'TeacherReplacedEmployeeId'
)
BEGIN
    ALTER TABLE TC_SubstitutePeriodEntries
        ADD TeacherReplacedEmployeeId INT NULL;
    PRINT '    Added column: TeacherReplacedEmployeeId';
END
ELSE PRINT '    Skipped: TeacherReplacedEmployeeId already exists';
GO

-------------------------------------------------------------------------------
-- 2. Foreign key to TC_Employees
-------------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = 'FK_TCSubPeriod_TeacherReplacedEmployee'
)
BEGIN
    ALTER TABLE TC_SubstitutePeriodEntries
        ADD CONSTRAINT FK_TCSubPeriod_TeacherReplacedEmployee
            FOREIGN KEY (TeacherReplacedEmployeeId)
            REFERENCES TC_Employees(EmployeeId)
            ON DELETE SET NULL;
    PRINT '    Added FK: FK_TCSubPeriod_TeacherReplacedEmployee';
END
ELSE PRINT '    Skipped: FK already exists';
GO

-------------------------------------------------------------------------------
-- 3. Supporting index for HR queries that filter on teacher replaced
-------------------------------------------------------------------------------
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TCSubPeriod_TeacherReplacedEmployeeId'
      AND object_id = OBJECT_ID('TC_SubstitutePeriodEntries')
)
BEGIN
    CREATE INDEX IX_TCSubPeriod_TeacherReplacedEmployeeId
        ON TC_SubstitutePeriodEntries(TeacherReplacedEmployeeId)
        WHERE TeacherReplacedEmployeeId IS NOT NULL;
    PRINT '    Added filtered index on TeacherReplacedEmployeeId';
END
ELSE PRINT '    Skipped: index already exists';
GO

-------------------------------------------------------------------------------
-- 4. Backfill from MasterScheduleId (opportunistic — only where we can
--    uniquely resolve Staff.Dcid → TC_Employees.EmployeeId).
--    Any row that already has TeacherReplacedEmployeeId set is left alone.
-------------------------------------------------------------------------------
DECLARE @Backfilled INT = 0;

;WITH resolvable AS (
    SELECT
        p.EntryId,
        e.EmployeeId
    FROM TC_SubstitutePeriodEntries p
    JOIN TC_MasterSchedule ms   ON ms.ScheduleId = p.MasterScheduleId
    JOIN TC_Employees e         ON e.StaffDcid = ms.TeacherStaffDcid AND e.IsActive = 1
    WHERE p.TeacherReplacedEmployeeId IS NULL
      AND p.MasterScheduleId IS NOT NULL
      AND ms.TeacherStaffDcid IS NOT NULL
)
UPDATE p
SET p.TeacherReplacedEmployeeId = r.EmployeeId
FROM TC_SubstitutePeriodEntries p
JOIN resolvable r ON r.EntryId = p.EntryId;

SET @Backfilled = @@ROWCOUNT;
PRINT '    Backfilled TeacherReplacedEmployeeId on ' + CAST(@Backfilled AS NVARCHAR(10)) + ' existing row(s).';
GO

-------------------------------------------------------------------------------
-- VERIFICATION
-------------------------------------------------------------------------------
PRINT '';
PRINT '=== Verification ===';
SELECT
    COUNT(*)                                                AS TotalEntries,
    SUM(CASE WHEN TeacherReplacedEmployeeId IS NOT NULL THEN 1 ELSE 0 END) AS WithEmployeeId,
    SUM(CASE WHEN TeacherReplaced           IS NOT NULL THEN 1 ELSE 0 END) AS WithTextName,
    SUM(CASE WHEN MasterScheduleId          IS NOT NULL THEN 1 ELSE 0 END) AS WithMasterScheduleLink
FROM TC_SubstitutePeriodEntries;

PRINT 'Migration 051 complete.';
GO
