/*
===============================================================================
New Heights TimeClock System - Patch Script
Version: 2.0b
Date: 2026-03-12
Database: IDSuite3 (Azure SQL)

Fixes two issues from migration 002:

  1. TC_AuditLog filtered indexes (PunchId, CorrectionId, EmployeeId, CampusId)
     failed with "Invalid column name" because they ran in a GO batch before
     the table was fully committed. This patch creates them if missing.

  2. TC_StaffHoursWindow only inserted McCart rows. Stop Six was skipped
     because the CampusCode in the DB did not match 'STOP6'. This patch
     detects the actual Stop Six campus code and inserts the missing row.

Safe to run multiple times (idempotent).
===============================================================================
*/

SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'TimeClock Patch 2.0b';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';
GO

-------------------------------------------------------------------------------
-- FIX 1: TC_AuditLog filtered indexes
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Fix 1: TC_AuditLog filtered indexes...';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TC_AuditLog') AND name = 'IX_TCAudit_PunchId')
BEGIN
    CREATE INDEX IX_TCAudit_PunchId ON TC_AuditLog(PunchId) WHERE PunchId IS NOT NULL;
    PRINT '    Created: IX_TCAudit_PunchId';
END
ELSE PRINT '    Skipped: IX_TCAudit_PunchId already exists';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TC_AuditLog') AND name = 'IX_TCAudit_CorrId')
BEGIN
    CREATE INDEX IX_TCAudit_CorrId ON TC_AuditLog(CorrectionId) WHERE CorrectionId IS NOT NULL;
    PRINT '    Created: IX_TCAudit_CorrId';
END
ELSE PRINT '    Skipped: IX_TCAudit_CorrId already exists';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TC_AuditLog') AND name = 'IX_TCAudit_EmployeeId')
BEGIN
    CREATE INDEX IX_TCAudit_EmployeeId ON TC_AuditLog(EmployeeId) WHERE EmployeeId IS NOT NULL;
    PRINT '    Created: IX_TCAudit_EmployeeId';
END
ELSE PRINT '    Skipped: IX_TCAudit_EmployeeId already exists';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TC_AuditLog') AND name = 'IX_TCAudit_CampusDate')
BEGIN
    CREATE INDEX IX_TCAudit_CampusDate ON TC_AuditLog(CampusId, CreatedDate) WHERE CampusId IS NOT NULL;
    PRINT '    Created: IX_TCAudit_CampusDate';
END
ELSE PRINT '    Skipped: IX_TCAudit_CampusDate already exists';
GO

-------------------------------------------------------------------------------
-- FIX 2: TC_StaffHoursWindow - Stop Six missing row
--
-- The 002 migration checked for CampusCode = 'STOP6' but the actual value
-- in the DB may differ. This fix finds the Stop Six campus by checking
-- multiple likely code values and inserts the window row if missing.
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Fix 2: TC_StaffHoursWindow - Stop Six campus...';

-- Show all campuses so we can confirm codes
PRINT '    Campus codes currently in Attendance_Campuses:';
SELECT '    >> ' + CampusCode + ' | ' + CampusName AS CampusInfo
FROM Attendance_Campuses;
GO

DECLARE @StopSixId INT = NULL;

-- Try all known variants of the Stop Six campus code
SELECT @StopSixId = CampusId
FROM Attendance_Campuses
WHERE CampusCode IN ('STOP6', 'STOPSIX', 'STOP_SIX', 'SS', 'S6')
   OR CampusName LIKE '%Stop Six%'
   OR CampusName LIKE '%StopSix%';

IF @StopSixId IS NULL
BEGIN
    PRINT '    WARNING: Could not find Stop Six campus.';
    PRINT '    Run this query to see your campus codes:';
    PRINT '      SELECT CampusId, CampusCode, CampusName FROM Attendance_Campuses;';
    PRINT '    Then manually insert the row:';
    PRINT '      INSERT INTO TC_StaffHoursWindow (CampusId, SessionType, SchoolYear,';
    PRINT '        ExpectedArrivalTime, ExpectedDepartureTime, Notes)';
    PRINT '      VALUES (<CampusId>, ''DAY'',   ''2025-2026'', ''07:30'', ''15:30'', ''Placeholder''),';
    PRINT '             (<CampusId>, ''NIGHT'', ''2025-2026'', ''16:30'', ''22:00'', ''Placeholder'');';
END
ELSE
BEGIN
    -- Insert DAY window if missing
    IF NOT EXISTS (
        SELECT 1 FROM TC_StaffHoursWindow
        WHERE CampusId = @StopSixId AND SessionType = 'DAY' AND SchoolYear = '2025-2026'
    )
    BEGIN
        INSERT INTO TC_StaffHoursWindow
            (CampusId, SessionType, SchoolYear, ExpectedArrivalTime, ExpectedDepartureTime, Notes)
        VALUES
            (@StopSixId, 'DAY', '2025-2026', '07:30', '15:30', 'Placeholder - confirm with admin');
        PRINT '    Inserted: Stop Six DAY window';
    END
    ELSE PRINT '    Skipped: Stop Six DAY window already exists';

    -- Insert NIGHT window if missing
    IF NOT EXISTS (
        SELECT 1 FROM TC_StaffHoursWindow
        WHERE CampusId = @StopSixId AND SessionType = 'NIGHT' AND SchoolYear = '2025-2026'
    )
    BEGIN
        INSERT INTO TC_StaffHoursWindow
            (CampusId, SessionType, SchoolYear, ExpectedArrivalTime, ExpectedDepartureTime, Notes)
        VALUES
            (@StopSixId, 'NIGHT', '2025-2026', '16:30', '22:00', 'Placeholder - confirm with admin');
        PRINT '    Inserted: Stop Six NIGHT window';
    END
    ELSE PRINT '    Skipped: Stop Six NIGHT window already exists';
END
GO

-------------------------------------------------------------------------------
-- Verify final state
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Verification: TC_StaffHoursWindow contents';
SELECT
    c.CampusCode,
    c.CampusName,
    w.SessionType,
    w.SchoolYear,
    CONVERT(VARCHAR(8), w.ExpectedArrivalTime, 108)   AS ArrivalTime,
    CONVERT(VARCHAR(8), w.ExpectedDepartureTime, 108) AS DepartureTime,
    w.Notes
FROM TC_StaffHoursWindow w
JOIN Attendance_Campuses c ON c.CampusId = w.CampusId
ORDER BY c.CampusCode, w.SessionType;
GO

PRINT '';
PRINT '>>> Verification: TC_AuditLog indexes';
SELECT name AS IndexName, filter_definition AS Filter
FROM sys.indexes
WHERE object_id = OBJECT_ID('TC_AuditLog')
  AND name LIKE 'IX_TCAudit%'
ORDER BY name;
GO

PRINT '';
PRINT '========================================';
PRINT 'Patch 2.0b Complete!';
PRINT 'Finished: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';
GO
