-- =============================================
-- Migration: 028 - Convert UTC to Local CDT
-- Date: 2026-04-07
-- Database: IDCardPrinterDB
-- Description: Converts all timestamps from UTC to local CDT time.
--              Subtracts 5 hours (CDT = UTC - 5).
--              After this, application will store/display local times only.
-- =============================================

USE IDCardPrinterDB;
GO

BEGIN TRANSACTION;

PRINT '========================================';
PRINT 'CONVERTING UTC TO LOCAL CDT';
PRINT '========================================';
PRINT '';
PRINT 'Current CDT offset: UTC - 5 hours';
PRINT '';

-- =============================================
-- PART 1: TC_TimePunches
-- =============================================

PRINT 'PART 1: Converting TC_TimePunches...';
PRINT '';

-- Show sample BEFORE
PRINT 'Sample BEFORE conversion (Jasmine):';
SELECT TOP 3
    PunchId,
    PunchDateTime AS Current_UTC,
    DATEADD(HOUR, -5, PunchDateTime) AS WillBe_Local_CDT,
    PunchType
FROM TC_TimePunches
WHERE EmployeeId = 1
ORDER BY PunchDateTime DESC;

DECLARE @PunchCount INT;
SELECT @PunchCount = COUNT(*) FROM TC_TimePunches;

PRINT '';
PRINT 'Total punches to convert: ' + CAST(@PunchCount AS VARCHAR(10));
PRINT 'Subtracting 5 hours (UTC -> CDT)...';
PRINT '';

-- Convert: Subtract 5 hours
UPDATE TC_TimePunches
SET 
    PunchDateTime = DATEADD(HOUR, -5, PunchDateTime),
    RoundedDateTime = DATEADD(HOUR, -5, RoundedDateTime);

PRINT '✓ Updated ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' punch records.';
PRINT '';

-- Show sample AFTER
PRINT 'Sample AFTER conversion (Jasmine):';
SELECT TOP 3
    PunchId,
    PunchDateTime AS Now_Local_CDT,
    PunchType
FROM TC_TimePunches
WHERE EmployeeId = 1
ORDER BY PunchDateTime DESC;

PRINT '';

-- =============================================
-- PART 2: TC_DailyTimecards
-- =============================================

PRINT 'PART 2: Converting TC_DailyTimecards...';
PRINT '';

UPDATE TC_DailyTimecards
SET 
    FirstPunchIn = DATEADD(HOUR, -5, FirstPunchIn),
    LastPunchOut = CASE 
        WHEN LastPunchOut IS NOT NULL THEN DATEADD(HOUR, -5, LastPunchOut)
        ELSE NULL
    END;

PRINT '✓ Updated ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' timecard records.';
PRINT '';

-- =============================================
-- PART 3: Attendance_Transactions
-- =============================================

PRINT 'PART 3: Converting Attendance_Transactions...';
PRINT '';

-- Show sample BEFORE
PRINT 'Sample BEFORE conversion (Staff):';
SELECT TOP 3
    TransactionId,
    FirstName + ' ' + LastName AS Name,
    ScanDateTime AS Current_UTC,
    DATEADD(HOUR, -5, ScanDateTime) AS WillBe_Local_CDT,
    ScanType
FROM Attendance_Transactions
WHERE TransactionType = 'STAFF'
ORDER BY ScanDateTime DESC;

PRINT '';

DECLARE @AttendanceCount INT;
SELECT @AttendanceCount = COUNT(*) FROM Attendance_Transactions;

PRINT 'Total attendance records to convert: ' + CAST(@AttendanceCount AS VARCHAR(10));
PRINT 'Subtracting 5 hours (UTC -> CDT)...';
PRINT '';

-- Convert: Subtract 5 hours
UPDATE Attendance_Transactions
SET ScanDateTime = DATEADD(HOUR, -5, ScanDateTime);

PRINT '✓ Updated ' + CAST(@@ROWCOUNT AS VARCHAR(10)) + ' attendance records.';
PRINT '';

-- Show sample AFTER
PRINT 'Sample AFTER conversion (Staff):';
SELECT TOP 3
    TransactionId,
    FirstName + ' ' + LastName AS Name,
    ScanDateTime AS Now_Local_CDT,
    ScanType
FROM Attendance_Transactions
WHERE TransactionType = 'STAFF'
ORDER BY ScanDateTime DESC;

PRINT '';
PRINT '========================================';
PRINT '✓ CONVERSION COMPLETE!';
PRINT '========================================';
PRINT '';
PRINT 'All timestamps now in local CDT time:';
PRINT '  • TC_TimePunches.PunchDateTime';
PRINT '  • TC_DailyTimecards.FirstPunchIn/LastPunchOut';
PRINT '  • Attendance_Transactions.ScanDateTime';
PRINT '';
PRINT 'Database values now match displayed values!';

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

-- Verify Jasmine's 7:51 AM punch
PRINT 'Jasmine Sanchez - Today''s punch:';
SELECT 
    p.PunchId,
    p.PunchDateTime AS Stored_Local_CDT,
    p.PunchType,
    e.IdNumber
FROM TC_TimePunches p
INNER JOIN TC_Employees e ON p.EmployeeId = e.EmployeeId
WHERE e.IdNumber = '116'
  AND p.PunchId = 129;

PRINT '';
PRINT 'Expected for 7:51 AM CDT punch:';
PRINT '  Stored_Local_CDT: 2026-04-07 07:51:04 ✓';
PRINT '';
PRINT 'If database value matches what you expect to see, SUCCESS!';
GO
