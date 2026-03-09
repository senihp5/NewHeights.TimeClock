-- ============================================
-- NewHeights TimeClock - Cleanup Test Data
-- WARNING: This removes ALL test data!
-- ============================================

SET NOCOUNT ON;
PRINT 'Cleaning up test data...';

-- Delete in order due to foreign keys
DELETE FROM TC_PayrollExports;
DELETE FROM TC_PayPeriodSummaries;
DELETE FROM TC_DailyTimecards;
DELETE FROM TC_TimePunches WHERE Notes = 'TEST DATA';
DELETE FROM TC_PayPeriods;
-- Optionally delete employees (comment out to keep)
-- DELETE FROM TC_Employees;

PRINT 'Test data cleanup complete.';

SELECT 'Remaining Employees' AS DataType, COUNT(*) AS Count FROM TC_Employees;
SELECT 'Remaining Punches' AS DataType, COUNT(*) AS Count FROM TC_TimePunches;
SELECT 'Remaining Timecards' AS DataType, COUNT(*) AS Count FROM TC_DailyTimecards;
