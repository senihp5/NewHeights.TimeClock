/*
===============================================================================
Seed: Bell Schedules for New Heights (McCart + Stop Six)
Date: 2026-04-22
Source: CLASS BELL SCHEDULES placard (Mon-Thurs Standard)

Creates one DAY schedule + one NIGHT schedule per campus, each flagged as
the default for that campus/session. Period rows P1-P4 (DAY) and P5-P6
(NIGHT) are CLASS-type and show up in the substitute picker. Lunch / Plan /
Break rows are kept for completeness with non-CLASS types so they do not
pollute the sub picker dropdown.

Idempotent: re-running the script is safe. A schedule with the same
(CampusId, SessionType, SchoolYear, ScheduleName) triple will NOT be
re-inserted, and its periods will NOT be duplicated.
===============================================================================
*/
SET NOCOUNT ON;
GO

DECLARE @Year      NVARCHAR(20)  = '2025-2026';
DECLARE @SchedName NVARCHAR(100) = 'Standard Mon-Thurs 2025-2026';
DECLARE @SeedUser  NVARCHAR(100) = 'seed:bell-schedules-2026-04-22';

DECLARE @McCart_Id   INT = (SELECT TOP 1 CampusId FROM Attendance_Campuses
                            WHERE CampusName LIKE '%McCart%' AND CampusCode <> 'DISTRICT'
                            ORDER BY CampusId);
DECLARE @StopSix_Id  INT = (SELECT TOP 1 CampusId FROM Attendance_Campuses
                            WHERE (CampusName LIKE '%Stop Six%' OR CampusCode = 'STOPSIX')
                              AND CampusCode <> 'DISTRICT'
                            ORDER BY CampusId);

IF @McCart_Id IS NULL
BEGIN
    RAISERROR('McCart campus not found. Aborting seed.', 16, 1);
    RETURN;
END
IF @StopSix_Id IS NULL
BEGIN
    RAISERROR('Stop Six campus not found. Aborting seed.', 16, 1);
    RETURN;
END

PRINT 'Resolved campuses:';
PRINT '  McCart   = ' + CAST(@McCart_Id AS NVARCHAR(10));
PRINT '  Stop Six = ' + CAST(@StopSix_Id AS NVARCHAR(10));
PRINT '';

-------------------------------------------------------------------------------
-- Helper: seed one schedule + its periods (inline, one block per combination)
-- The pattern: INSERT TC_BellSchedule if absent → capture @SchedId → INSERT
-- TC_BellPeriods for any missing period numbers.
-------------------------------------------------------------------------------

DECLARE @SchedId INT;

-------------------------------------------------------------------------------
-- 1. McCart — DAY
-------------------------------------------------------------------------------
PRINT '>> McCart / DAY';
SELECT @SchedId = BellScheduleId
FROM TC_BellSchedule
WHERE CampusId = @McCart_Id
  AND SessionType = 'DAY'
  AND SchoolYear = @Year
  AND ScheduleName = @SchedName;

IF @SchedId IS NULL
BEGIN
    INSERT INTO TC_BellSchedule
        (CampusId, SchoolYear, ScheduleName, SessionType, ScheduleType,
         IsDefault, IsActive, Notes, UploadedBy, UploadedDate, CreatedDate, ModifiedDate)
    VALUES
        (@McCart_Id, @Year, @SchedName, 'DAY', 'STANDARD',
         1, 1, 'Seeded from Class Bell Schedules placard', @SeedUser, GETDATE(), GETDATE(), GETDATE());
    SET @SchedId = SCOPE_IDENTITY();
    PRINT '    inserted schedule #' + CAST(@SchedId AS NVARCHAR(10));
END
ELSE PRINT '    skipped (exists #' + CAST(@SchedId AS NVARCHAR(10)) + ')';

-- CLASS periods (shown in sub picker)
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 1)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 1, 'Period 1', 'CLASS', '08:20:00', '09:45:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 2)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 2, 'Period 2', 'CLASS', '09:50:00', '11:15:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 3)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 3, 'Period 3', 'CLASS', '11:20:00', '12:45:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 4)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 4, 'Period 4', 'CLASS', '13:15:00', '14:40:00', 5, 20, 1);
-- Non-CLASS periods (hidden from sub picker; kept for completeness)
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 91)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 91, 'Lunch', 'LUNCH', '12:45:00', '13:15:00', 0, 0, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 92)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 92, 'Plan/Tutor', 'ADVISORY', '14:40:00', '16:15:00', 0, 0, 1);

-------------------------------------------------------------------------------
-- 2. McCart — NIGHT
-------------------------------------------------------------------------------
PRINT '>> McCart / NIGHT';
SET @SchedId = NULL;
SELECT @SchedId = BellScheduleId
FROM TC_BellSchedule
WHERE CampusId = @McCart_Id AND SessionType = 'NIGHT'
  AND SchoolYear = @Year AND ScheduleName = @SchedName;

IF @SchedId IS NULL
BEGIN
    INSERT INTO TC_BellSchedule
        (CampusId, SchoolYear, ScheduleName, SessionType, ScheduleType,
         IsDefault, IsActive, Notes, UploadedBy, UploadedDate, CreatedDate, ModifiedDate)
    VALUES
        (@McCart_Id, @Year, @SchedName, 'NIGHT', 'STANDARD',
         1, 1, 'Seeded from Class Bell Schedules placard', @SeedUser, GETDATE(), GETDATE(), GETDATE());
    SET @SchedId = SCOPE_IDENTITY();
    PRINT '    inserted schedule #' + CAST(@SchedId AS NVARCHAR(10));
END
ELSE PRINT '    skipped (exists #' + CAST(@SchedId AS NVARCHAR(10)) + ')';

IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 5)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 5, 'Period 5', 'CLASS', '18:15:00', '19:40:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 6)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 6, 'Period 6', 'CLASS', '19:50:00', '21:15:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 93)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 93, 'Plan/Tutor', 'ADVISORY', '17:30:00', '18:15:00', 0, 0, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 94)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 94, 'Break/Dinner', 'BREAK', '19:40:00', '19:50:00', 0, 0, 1);

-------------------------------------------------------------------------------
-- 3. Stop Six — DAY (identical times to McCart)
-------------------------------------------------------------------------------
PRINT '>> Stop Six / DAY';
SET @SchedId = NULL;
SELECT @SchedId = BellScheduleId
FROM TC_BellSchedule
WHERE CampusId = @StopSix_Id AND SessionType = 'DAY'
  AND SchoolYear = @Year AND ScheduleName = @SchedName;

IF @SchedId IS NULL
BEGIN
    INSERT INTO TC_BellSchedule
        (CampusId, SchoolYear, ScheduleName, SessionType, ScheduleType,
         IsDefault, IsActive, Notes, UploadedBy, UploadedDate, CreatedDate, ModifiedDate)
    VALUES
        (@StopSix_Id, @Year, @SchedName, 'DAY', 'STANDARD',
         1, 1, 'Seeded from Class Bell Schedules placard', @SeedUser, GETDATE(), GETDATE(), GETDATE());
    SET @SchedId = SCOPE_IDENTITY();
    PRINT '    inserted schedule #' + CAST(@SchedId AS NVARCHAR(10));
END
ELSE PRINT '    skipped (exists #' + CAST(@SchedId AS NVARCHAR(10)) + ')';

IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 1)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 1, 'Period 1', 'CLASS', '08:20:00', '09:45:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 2)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 2, 'Period 2', 'CLASS', '09:50:00', '11:15:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 3)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 3, 'Period 3', 'CLASS', '11:20:00', '12:45:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 4)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 4, 'Period 4', 'CLASS', '13:15:00', '14:40:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 91)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 91, 'Lunch', 'LUNCH', '12:45:00', '13:15:00', 0, 0, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 92)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 92, 'Plan/Tutor', 'ADVISORY', '14:40:00', '16:15:00', 0, 0, 1);

-------------------------------------------------------------------------------
-- 4. Stop Six — NIGHT (identical times to McCart)
-------------------------------------------------------------------------------
PRINT '>> Stop Six / NIGHT';
SET @SchedId = NULL;
SELECT @SchedId = BellScheduleId
FROM TC_BellSchedule
WHERE CampusId = @StopSix_Id AND SessionType = 'NIGHT'
  AND SchoolYear = @Year AND ScheduleName = @SchedName;

IF @SchedId IS NULL
BEGIN
    INSERT INTO TC_BellSchedule
        (CampusId, SchoolYear, ScheduleName, SessionType, ScheduleType,
         IsDefault, IsActive, Notes, UploadedBy, UploadedDate, CreatedDate, ModifiedDate)
    VALUES
        (@StopSix_Id, @Year, @SchedName, 'NIGHT', 'STANDARD',
         1, 1, 'Seeded from Class Bell Schedules placard', @SeedUser, GETDATE(), GETDATE(), GETDATE());
    SET @SchedId = SCOPE_IDENTITY();
    PRINT '    inserted schedule #' + CAST(@SchedId AS NVARCHAR(10));
END
ELSE PRINT '    skipped (exists #' + CAST(@SchedId AS NVARCHAR(10)) + ')';

IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 5)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 5, 'Period 5', 'CLASS', '18:15:00', '19:40:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 6)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 6, 'Period 6', 'CLASS', '19:50:00', '21:15:00', 5, 20, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 93)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 93, 'Plan/Tutor', 'ADVISORY', '17:30:00', '18:15:00', 0, 0, 1);
IF NOT EXISTS (SELECT 1 FROM TC_BellPeriods WHERE BellScheduleId = @SchedId AND PeriodNumber = 94)
    INSERT INTO TC_BellPeriods (BellScheduleId, PeriodNumber, PeriodName, PeriodType, StartTime, EndTime, TardyThresholdMinutes, AbsentThresholdMinutes, IsActive)
    VALUES (@SchedId, 94, 'Break/Dinner', 'BREAK', '19:40:00', '19:50:00', 0, 0, 1);

PRINT '';
PRINT '=== Verification ===';
SELECT
    c.CampusName,
    s.SessionType,
    s.ScheduleName,
    s.IsDefault,
    s.IsActive,
    (SELECT COUNT(*) FROM TC_BellPeriods p
     WHERE p.BellScheduleId = s.BellScheduleId AND p.PeriodType = 'CLASS') AS ClassPeriodCount
FROM TC_BellSchedule s
JOIN Attendance_Campuses c ON c.CampusId = s.CampusId
WHERE s.CampusId IN (@McCart_Id, @StopSix_Id)
  AND s.SchoolYear = @Year
ORDER BY c.CampusName, s.SessionType;
GO
