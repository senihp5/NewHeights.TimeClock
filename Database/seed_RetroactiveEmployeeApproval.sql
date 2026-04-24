/*
===============================================================================
Retroactive Employee-Approve Imported Timesheets
Date: 2026-04-23
Purpose:
  Historical pay periods backfilled via the Hourly CSV Import (CSV_IMPORT)
  need to land in the supervisor approval queue as "employee-approved".
  The admin is importing signed paper timesheets on the employee's behalf;
  the employee has already signed at the source. This script sets
  TC_DailyTimecards.ApprovalStatus = 'Approved' for every day that (a) has
  a CSV-imported punch AND (b) falls BEFORE the current pay period.

  Future imports auto-approve via HourlyCsvImportService.ApplyAsync; this
  is the one-shot patch for imports that ran before that wiring landed.

Idempotent. Skips days already Approved / Locked.
===============================================================================
*/
SET NOCOUNT ON;
GO

BEGIN TRANSACTION;

-- Identify the current pay period boundary so we don't approve the in-progress week.
-- If no current pay period is defined, fall back to today.
DECLARE @CurrentPeriodStart DATE;
SELECT TOP 1 @CurrentPeriodStart = StartDate
FROM TC_PayPeriods
WHERE CAST(GETDATE() AS DATE) BETWEEN StartDate AND EndDate
ORDER BY StartDate DESC;

IF @CurrentPeriodStart IS NULL SET @CurrentPeriodStart = CAST(GETDATE() AS DATE);

PRINT 'Current pay period starts: ' + CONVERT(VARCHAR, @CurrentPeriodStart, 120);

-- Find every (employee, date) that has at least one CSV_IMPORT punch AND is
-- strictly before the current pay period.
;WITH imported_days AS (
    SELECT DISTINCT
        p.EmployeeId,
        CAST(p.PunchDateTime AS DATE) AS WorkDate
    FROM TC_TimePunches p
    WHERE p.PunchSource = 'CSV_IMPORT'
      AND p.PunchStatus = 'Active'
)
UPDATE dt
SET dt.ApprovalStatus = 'Approved',
    dt.ApprovedBy     = 'retroactive:seed-2026-04-23',
    dt.ApprovedDate   = GETDATE(),
    dt.ModifiedDate   = GETDATE()
FROM TC_DailyTimecards dt
JOIN imported_days i
  ON i.EmployeeId = dt.EmployeeId
 AND i.WorkDate  = dt.WorkDate
WHERE dt.WorkDate < @CurrentPeriodStart
  AND dt.ApprovalStatus NOT IN ('Approved', 'Locked');

DECLARE @Updated INT = @@ROWCOUNT;
PRINT 'Retroactively approved ' + CAST(@Updated AS NVARCHAR(10)) + ' daily timecard row(s).';

-- Preview what was touched (within last 5 minutes by this marker)
SELECT dt.EmployeeId, dt.WorkDate, dt.ApprovalStatus, dt.TotalHours, dt.ApprovedBy
FROM TC_DailyTimecards dt
WHERE dt.ApprovedBy = 'retroactive:seed-2026-04-23'
  AND dt.ModifiedDate >= DATEADD(MINUTE, -5, GETDATE())
ORDER BY dt.EmployeeId, dt.WorkDate;

-- COMMIT or ROLLBACK based on the count + preview
-- COMMIT;
-- ROLLBACK;
