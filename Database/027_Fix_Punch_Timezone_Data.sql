-- =============================================
-- Migration: 027 - Fix Punch Timezone Data
-- Date: 2026-04-07
-- Database: IDCardPrinterDB
-- Description: Corrects TC_TimePunches.PunchDateTime values that were 
--              incorrectly stored as CST instead of UTC.
--              Adds 6 hours to convert CST -> UTC.
-- =============================================

-- WARNING: This migration modifies historical punch data.
-- Azure SQL maintains automatic backups - you can restore to any point in time if needed.

USE IDCardPrinterDB;
GO

BEGIN TRANSACTION;

PRINT 'Starting timezone correction for TC_TimePunches...';
PRINT 'Database: IDCardPrinterDB';
PRINT '';

-- Display affected records before correction
PRINT 'Sample punches that will be corrected:';
SELECT TOP 10
    PunchId,
    EmployeeId,
    PunchDateTime AS CurrentTime_StoredAsCST,
    DATEADD(HOUR, 6, PunchDateTime) AS CorrectedTime_WillBeUTC,
    PunchType,
    CreatedDate
FROM TC_TimePunches
WHERE PunchDateTime < DATEADD(HOUR, 6, GETUTCDATE())
ORDER BY PunchDateTime DESC;

-- Count total records to be updated
DECLARE @PunchCount INT;
DECLARE @TimecardCount INT;

SELECT @PunchCount = COUNT(*) 
FROM TC_TimePunches
WHERE PunchDateTime < DATEADD(HOUR, 6, GETUTCDATE());

SELECT @TimecardCount = COUNT(*)
FROM TC_DailyTimecards
WHERE FirstPunchIn < DATEADD(HOUR, 6, GETUTCDATE());

PRINT '';
PRINT 'Total punches to correct: ' + CAST(@PunchCount AS VARCHAR(10));
PRINT 'Total timecards to update: ' + CAST(@TimecardCount AS VARCHAR(10));
PRINT '';
PRINT 'Applying corrections...';

-- Update PunchDateTime: Add 6 hours to convert CST -> UTC
-- Only update punches where the time is clearly wrong (before "now + 6 hours")
UPDATE TC_TimePunches
SET 
    PunchDateTime = DATEADD(HOUR, 6, PunchDateTime),
    RoundedDateTime = DATEADD(HOUR, 6, RoundedDateTime),
    ModifiedDate = GETUTCDATE()
WHERE PunchDateTime < DATEADD(HOUR, 6, GETUTCDATE());

PRINT 'Updated ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' punch records.';

-- Update daily timecard first/last punch times
PRINT 'Updating daily timecard punch references...';

UPDATE TC_DailyTimecards
SET 
    FirstPunchIn = DATEADD(HOUR, 6, FirstPunchIn),
    LastPunchOut = CASE 
        WHEN LastPunchOut IS NOT NULL AND LastPunchOut < DATEADD(HOUR, 6, GETUTCDATE()) 
        THEN DATEADD(HOUR, 6, LastPunchOut)
        ELSE LastPunchOut
    END,
    ModifiedDate = GETUTCDATE()
WHERE FirstPunchIn < DATEADD(HOUR, 6, GETUTCDATE());

PRINT 'Updated ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' timecard records.';

-- Verify correction with sample
PRINT '';
PRINT 'Sample of corrected records:';
SELECT TOP 10
    PunchId,
    EmployeeId,
    PunchDateTime AS CorrectedTime_NowUTC,
    DATEADD(HOUR, -6, PunchDateTime) AS DisplayInCST,
    PunchType,
    ModifiedDate
FROM TC_TimePunches
ORDER BY ModifiedDate DESC;

PRINT '';
PRINT '✓ Timezone correction complete!';
PRINT 'All PunchDateTime values are now stored in UTC.';
PRINT 'Times will display correctly in CST on reports.';

COMMIT TRANSACTION;
GO

-- =============================================
-- VERIFICATION QUERIES (run these after migration)
-- =============================================

PRINT '';
PRINT 'Running verification checks...';
PRINT '';

-- Check Jasmine Sanchez's corrected punches (ID 116)
PRINT 'Jasmine Sanchez recent punches (ID: 116):';
SELECT TOP 5
    p.PunchId,
    p.PunchDateTime AS StoredInUTC,
    DATEADD(HOUR, -6, p.PunchDateTime) AS DisplayInCST,
    p.PunchType,
    e.IdNumber,
    s.FirstName + ' ' + s.LastName AS EmployeeName
FROM TC_TimePunches p
INNER JOIN TC_Employees e ON p.EmployeeId = e.EmployeeId
LEFT JOIN Staff s ON e.StaffDcid = s.DCID
WHERE e.IdNumber = '116'
ORDER BY p.PunchDateTime DESC;

PRINT '';
PRINT 'Verification complete!';
PRINT '';
PRINT 'Expected results for today''s 8:01 AM punch:';
PRINT '  StoredInUTC: 2026-04-07 14:01:xx (8:01 AM CST = 14:01 UTC)';
PRINT '  DisplayInCST: 2026-04-07 08:01:xx ✓';
GO
