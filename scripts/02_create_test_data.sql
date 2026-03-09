-- ============================================
-- NewHeights TimeClock Test Data Script
-- Run this in SSMS against IDCardPrinterDB
-- Creates sample hourly employees with time punches
-- ============================================

SET NOCOUNT ON;
PRINT 'Starting TimeClock test data creation...';

-- ============================================
-- STEP 1: Create Test Employees (if not exist)
-- ============================================

-- First check if we have any TC_Employees
IF NOT EXISTS (SELECT 1 FROM TC_Employees)
BEGIN
    PRINT 'Creating test employees...';
    
    -- Get a supervisor (you - Patrick Hines)
    DECLARE @SupervisorDcid INT;
    DECLARE @SupervisorId INT;
    
    SELECT TOP 1 @SupervisorDcid = StaffDcid 
    FROM Staff 
    WHERE Email_Addr LIKE '%phines%' OR LastName = 'Hines';
    
    -- If not found, get any active staff
    IF @SupervisorDcid IS NULL
        SELECT TOP 1 @SupervisorDcid = StaffDcid FROM Staff WHERE Status = 1;
    
    -- Get default campus
    DECLARE @DefaultCampusId INT = 1;
    SELECT TOP 1 @DefaultCampusId = CampusId FROM Attendance_Campuses;
    
    -- Insert supervisor as employee first
    INSERT INTO TC_Employees (StaffDcid, IdNumber, EmployeeType, HomeCampusId, Email, IsActive, CreatedDate, ModifiedDate)
    SELECT @SupervisorDcid, 'SUP001', 2, @DefaultCampusId, Email_Addr, 1, GETUTCDATE(), GETUTCDATE()
    FROM Staff WHERE StaffDcid = @SupervisorDcid;
    
    SET @SupervisorId = SCOPE_IDENTITY();
    PRINT 'Created supervisor with ID: ' + CAST(@SupervisorId AS VARCHAR);
    
    -- Create 5 hourly test employees
    DECLARE @i INT = 1;
    DECLARE @StaffDcid INT;
    
    WHILE @i <= 5
    BEGIN
        -- Get a random staff member
        SELECT TOP 1 @StaffDcid = s.StaffDcid 
        FROM Staff s
        WHERE s.Status = 1 
          AND NOT EXISTS (SELECT 1 FROM TC_Employees e WHERE e.StaffDcid = s.StaffDcid)
        ORDER BY NEWID();
        
        IF @StaffDcid IS NOT NULL
        BEGIN
            INSERT INTO TC_Employees (StaffDcid, IdNumber, EmployeeType, HomeCampusId, SupervisorEmployeeId, Email, IsActive, CreatedDate, ModifiedDate)
            SELECT @StaffDcid, 'HRL' + RIGHT('000' + CAST(@i AS VARCHAR), 3), 
                   2, -- HourlyStaff
                   @DefaultCampusId, 
                   @SupervisorId,
                   Email_Addr, 
                   1, GETUTCDATE(), GETUTCDATE()
            FROM Staff WHERE StaffDcid = @StaffDcid;
            
            PRINT 'Created hourly employee #' + CAST(@i AS VARCHAR);
        END
        
        SET @i = @i + 1;
    END
END
ELSE
BEGIN
    PRINT 'Employees already exist, skipping creation...';
END

-- ============================================
-- STEP 2: Create Time Punches for Current Pay Period
-- ============================================

PRINT 'Creating time punches for current pay period...';

-- Determine current pay period dates
DECLARE @Today DATE = GETDATE();
DECLARE @PeriodStart DATE;
DECLARE @PeriodEnd DATE;

IF DAY(@Today) <= 15
BEGIN
    SET @PeriodStart = DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 1);
    SET @PeriodEnd = DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 15);
END
ELSE
BEGIN
    SET @PeriodStart = DATEFROMPARTS(YEAR(@Today), MONTH(@Today), 16);
    SET @PeriodEnd = EOMONTH(@Today);
END

PRINT 'Pay period: ' + CAST(@PeriodStart AS VARCHAR) + ' to ' + CAST(@PeriodEnd AS VARCHAR);

-- Clear existing test punches for this period (optional - comment out to keep)
-- DELETE FROM TC_TimePunches WHERE PunchDateTime >= @PeriodStart AND Notes LIKE '%TEST%';

-- Create punches for each hourly employee
DECLARE @EmployeeId INT;
DECLARE @CampusId INT;
DECLARE @WorkDate DATE;

DECLARE emp_cursor CURSOR FOR
SELECT EmployeeId, HomeCampusId 
FROM TC_Employees 
WHERE EmployeeType = 2 AND IsActive = 1; -- HourlyStaff

OPEN emp_cursor;
FETCH NEXT FROM emp_cursor INTO @EmployeeId, @CampusId;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @WorkDate = @PeriodStart;
    
    WHILE @WorkDate <= @Today AND @WorkDate <= @PeriodEnd
    BEGIN
        -- Skip weekends
        IF DATEPART(WEEKDAY, @WorkDate) NOT IN (1, 7) -- Not Sunday or Saturday
        BEGIN
            -- Check if punches already exist for this day
            IF NOT EXISTS (SELECT 1 FROM TC_TimePunches WHERE EmployeeId = @EmployeeId AND CAST(PunchDateTime AS DATE) = @WorkDate)
            BEGIN
                -- Morning clock in (7:45 - 8:15 AM random)
                DECLARE @ClockIn DATETIME = DATEADD(MINUTE, ABS(CHECKSUM(NEWID())) % 30 + 465, CAST(@WorkDate AS DATETIME)); -- 7:45 + 0-30 min
                
                -- Lunch out (11:45 - 12:15 PM)
                DECLARE @LunchOut DATETIME = DATEADD(MINUTE, ABS(CHECKSUM(NEWID())) % 30 + 705, CAST(@WorkDate AS DATETIME)); -- 11:45 + 0-30 min
                
                -- Lunch in (12:30 - 1:00 PM)
                DECLARE @LunchIn DATETIME = DATEADD(MINUTE, ABS(CHECKSUM(NEWID())) % 30 + 750, CAST(@WorkDate AS DATETIME)); -- 12:30 + 0-30 min
                
                -- Clock out (4:45 - 5:15 PM)
                DECLARE @ClockOut DATETIME = DATEADD(MINUTE, ABS(CHECKSUM(NEWID())) % 30 + 1005, CAST(@WorkDate AS DATETIME)); -- 4:45 + 0-30 min
                
                -- Insert punches
                INSERT INTO TC_TimePunches (EmployeeId, CampusId, PunchType, PunchDateTime, RoundedDateTime, VerificationMethod, ScanMethod, IsManualEntry, PunchStatus, Notes, CreatedDate)
                VALUES 
                    (@EmployeeId, @CampusId, 0, @ClockIn, @ClockIn, 'TEST', 'SCRIPT', 0, 0, 'TEST DATA', GETUTCDATE()),
                    (@EmployeeId, @CampusId, 3, @LunchOut, @LunchOut, 'TEST', 'SCRIPT', 0, 0, 'TEST DATA', GETUTCDATE()),
                    (@EmployeeId, @CampusId, 4, @LunchIn, @LunchIn, 'TEST', 'SCRIPT', 0, 0, 'TEST DATA', GETUTCDATE()),
                    (@EmployeeId, @CampusId, 1, @ClockOut, @ClockOut, 'TEST', 'SCRIPT', 0, 0, 'TEST DATA', GETUTCDATE());
            END
        END
        
        SET @WorkDate = DATEADD(DAY, 1, @WorkDate);
    END
    
    FETCH NEXT FROM emp_cursor INTO @EmployeeId, @CampusId;
END

CLOSE emp_cursor;
DEALLOCATE emp_cursor;

PRINT 'Time punches created.';

-- ============================================
-- STEP 3: Create Daily Timecards from Punches
-- ============================================

PRINT 'Creating daily timecards...';

INSERT INTO TC_DailyTimecards (EmployeeId, WorkDate, RegularHours, OvertimeHours, TotalHours, ApprovalStatus, HasException, CreatedDate, ModifiedDate)
SELECT 
    p.EmployeeId,
    CAST(p.PunchDateTime AS DATE) AS WorkDate,
    8.0 AS RegularHours, -- Simplified: assume 8 hours
    0.0 AS OvertimeHours,
    8.0 AS TotalHours,
    0 AS ApprovalStatus, -- Pending
    0 AS HasException,
    GETUTCDATE(),
    GETUTCDATE()
FROM TC_TimePunches p
WHERE p.PunchType = 0 -- Clock In
  AND NOT EXISTS (
    SELECT 1 FROM TC_DailyTimecards t 
    WHERE t.EmployeeId = p.EmployeeId 
      AND t.WorkDate = CAST(p.PunchDateTime AS DATE)
  )
GROUP BY p.EmployeeId, CAST(p.PunchDateTime AS DATE);

PRINT 'Daily timecards created.';

-- ============================================
-- STEP 4: Add some variety - exceptions and partial days
-- ============================================

-- Mark one employee with an exception (missed punch)
UPDATE TOP (2) TC_DailyTimecards
SET HasException = 1, 
    ExceptionNotes = 'Missed clock out - needs supervisor review'
WHERE EmployeeId = (SELECT TOP 1 EmployeeId FROM TC_Employees WHERE EmployeeType = 2 ORDER BY NEWID())
  AND HasException = 0;

-- Mark one employee as having submitted their timesheet
UPDATE TC_DailyTimecards
SET ApprovalStatus = 1, -- Approved (employee submitted)
    ApprovedBy = 'employee@test.com',
    ApprovedDate = GETUTCDATE()
WHERE EmployeeId = (SELECT TOP 1 EmployeeId FROM TC_Employees WHERE EmployeeType = 2 ORDER BY EmployeeId)
  AND ApprovalStatus = 0;

PRINT 'Added exceptions and approvals.';

-- ============================================
-- STEP 5: Verify Results
-- ============================================

PRINT '';
PRINT '========== TEST DATA SUMMARY ==========';
PRINT '';

SELECT 'Employees Created' AS DataType, COUNT(*) AS Count FROM TC_Employees;
SELECT 'Hourly Employees' AS DataType, COUNT(*) AS Count FROM TC_Employees WHERE EmployeeType = 2;
SELECT 'Time Punches' AS DataType, COUNT(*) AS Count FROM TC_TimePunches WHERE Notes = 'TEST DATA';
SELECT 'Daily Timecards' AS DataType, COUNT(*) AS Count FROM TC_DailyTimecards;
SELECT 'With Exceptions' AS DataType, COUNT(*) AS Count FROM TC_DailyTimecards WHERE HasException = 1;

PRINT '';
PRINT 'Employee List:';
SELECT 
    e.EmployeeId,
    e.IdNumber,
    s.FirstName + ' ' + s.LastName AS EmployeeName,
    e.Email,
    CASE e.EmployeeType 
        WHEN 0 THEN 'Staff'
        WHEN 1 THEN 'Teacher'
        WHEN 2 THEN 'HourlyStaff'
        WHEN 3 THEN 'SalariedStaff'
        WHEN 4 THEN 'Admin'
        ELSE 'Other'
    END AS EmployeeType,
    sup.IdNumber AS SupervisorId
FROM TC_Employees e
JOIN Staff s ON e.StaffDcid = s.StaffDcid
LEFT JOIN TC_Employees sup ON e.SupervisorEmployeeId = sup.EmployeeId
ORDER BY e.EmployeeId;

PRINT '';
PRINT 'Recent Punches:';
SELECT TOP 20
    e.IdNumber,
    s.FirstName + ' ' + s.LastName AS EmployeeName,
    CASE p.PunchType 
        WHEN 0 THEN 'Clock In'
        WHEN 1 THEN 'Clock Out'
        WHEN 3 THEN 'Lunch Out'
        WHEN 4 THEN 'Lunch In'
        ELSE 'Other'
    END AS PunchType,
    p.PunchDateTime,
    p.Notes
FROM TC_TimePunches p
JOIN TC_Employees e ON p.EmployeeId = e.EmployeeId
JOIN Staff s ON e.StaffDcid = s.StaffDcid
ORDER BY p.PunchDateTime DESC;

PRINT '';
PRINT 'Test data creation complete!';
PRINT 'Navigate to /supervisor/timesheets to see the results.';
