-- =============================================
-- Migration: 027_v2 - Fix Punch Timezone Data (Complete Fix)
-- Date: 2026-04-07
-- Database: IDCardPrinterDB
-- Description: Corrects ALL TC_TimePunches.PunchDateTime values that were 
--              incorrectly stored as CST instead of UTC.
--              Also corrects AttendanceTransactions.ScanDateTime.
--              Adds 6 hours to convert CST -> UTC.
-- =============================================

USE IDCardPrinterDB;
GO

BEGIN TRANSACTION;

PRINT '========================================';
PRINT 'TIMEZONE CORRECTION - COMPLETE FIX';
PRINT '========================================';
PRINT '';

-- =============================================
-- PART 1: Fix TC_TimePunches
-- =============================================

PRINT 'PART 1: Correcting TC_TimePunches table...';
PRINT '';

-- Show sample BEFORE correction
PRINT 'Sample punches BEFORE correction:';
SELECT TOP 5
    PunchId,
    EmployeeId,
    PunchDateTime AS CurrentTime_CST,
    DATEADD(HOUR, 6, PunchDateTime) AS WillBe_UTC,
    PunchType
FROM TC_TimePunches
WHERE PunchId IN (129, 124, 122, 111)  -- Jasmine's recent punches
ORDER BY PunchDateTime DESC;

-- Count records to update
DECLARE @PunchCount INT;
SELECT @PunchCount = COUNT(*) FROM TC_TimePunches;

PRINT '';
PRINT 'Total punch records to correct: ' + CAST(@PunchCount AS VARCHAR(10));
PRINT 'Applying 6-hour offset (CST -> UTC)...';
PRINT '';

-- Update ALL punches (remove restrictive WHERE clause)
UPDATE TC_TimePunches
SET 
    PunchDateTime = DATEADD(HOUR, 6, PunchDateTime),
    RoundedDateTime = DATEADD(HOUR, 6, RoundedDateTime)
WHERE PunchId > 0;  -- Update all records

PRINT '✓ Updated ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' punch records.';
PRINT '';

-- Show sample AFTER correction
PRINT 'Sample punches AFTER correction:';
SELECT TOP 5
    PunchId,
    EmployeeId,
    PunchDateTime AS Now_UTC,
    DATEADD(HOUR, -6, PunchDateTime) AS Display_CST,
    PunchType
FROM TC_TimePunches
WHERE PunchId IN (129, 124, 122, 111)
ORDER BY PunchDateTime DESC;

PRINT '';

-- =============================================
-- PART 2: Fix TC_DailyTimecards
-- =============================================

PRINT 'PART 2: Correcting TC_DailyTimecards table...';
PRINT '';

UPDATE TC_DailyTimecards
SET 
    FirstPunchIn = DATEADD(HOUR, 6, FirstPunchIn),
    LastPunchOut = CASE 
        WHEN LastPunchOut IS NOT NULL THEN DATEADD(HOUR, 6, LastPunchOut)
        ELSE NULL
    END
WHERE WorkDate >= '2026-03-01';  -- Only recent data

PRINT '✓ Updated ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' timecard records.';
PRINT '';

-- =============================================
-- PART 3: Fix AttendanceTransactions (Staff Check-ins)
-- =============================================

PRINT 'PART 3: Correcting AttendanceTransactions table...';
PRINT '';

-- Show sample BEFORE
PRINT 'Sample attendance BEFORE correction:';
SELECT TOP 5
    TransactionId,
    TransactionType,
    FirstName + ' ' + LastName AS Name,
    ScanDateTime AS Current_CST,
    DATEADD(HOUR, 6, ScanDateTime) AS WillBe_UTC,
    ScanType
FROM AttendanceTransactions
WHERE TransactionType IN ('STAFF', 'STUDENT')
ORDER BY ScanDateTime DESC;

PRINT '';

DECLARE @AttendanceCount INT;
SELECT @AttendanceCount = COUNT(*) FROM AttendanceTransactions;

PRINT 'Total attendance records to correct: ' + CAST(@AttendanceCount AS VARCHAR(10));
PRINT 'Applying 6-hour offset (CST -> UTC)...';
PRINT '';

-- Update ALL attendance transactions
UPDATE AttendanceTransactions
SET ScanDateTime = DATEADD(HOUR, 6, ScanDateTime)
WHERE TransactionId > 0;

PRINT '✓ Updated ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' attendance records.';
PRINT '';

-- Show sample AFTER
PRINT 'Sample attendance AFTER correction:';
SELECT TOP 5
    TransactionId,
    TransactionType,
    FirstName + ' ' + LastName AS Name,
    ScanDateTime AS Now_UTC,
    DATEADD(HOUR, -6, ScanDateTime) AS Display_CST,
    ScanType
FROM AttendanceTransactions
WHERE TransactionType IN ('STAFF', 'STUDENT')
ORDER BY ScanDateTime DESC;

PRINT '';
PRINT '========================================';
PRINT '✓ TIMEZONE CORRECTION COMPLETE!';
PRINT '========================================';
PRINT '';
PRINT 'All timestamps now stored in UTC:';
PRINT '  • TC_TimePunches.PunchDateTime';
PRINT '  • TC_DailyTimecards.FirstPunchIn/LastPunchOut';
PRINT '  • AttendanceTransactions.ScanDateTime';
PRINT '';
PRINT 'Reports will display times in CST automatically.';

COMMIT TRANSACTION;
GO

-- =============================================
-- VERIFICATION
-- =============================================

PRINT '';
PRINT '========================================';
PRINT 'VERIFICATION';
PRINT '========================================';
PRINT '';

-- Verify Jasmine's today's 8:01 AM punch
PRINT 'Jasmine Sanchez - Today''s 8:01 AM punch (ID: 116):';
SELECT 
    p.PunchId,
    p.PunchDateTime AS Stored_UTC,
    DATEADD(HOUR, -6, p.PunchDateTime) AS Display_CST,
    p.PunchType,
    e.IdNumber
FROM TC_TimePunches p
INNER JOIN TC_Employees e ON p.EmployeeId = e.EmployeeId
WHERE e.IdNumber = '116'
  AND p.PunchDateTime >= '2026-04-07'
ORDER BY p.PunchDateTime DESC;

PRINT '';
PRINT 'Expected for 8:01 AM CST punch:';
PRINT '  Stored_UTC:  2026-04-07 14:01:xx ✓';
PRINT '  Display_CST: 2026-04-07 08:01:xx ✓';
PRINT '';
PRINT 'If you see these values above, the correction was successful!';
GO
