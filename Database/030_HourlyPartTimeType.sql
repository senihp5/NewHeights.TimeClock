-- Migration 030: Document HourlyPartTime employee type
-- Date: 2026-04-16
-- Purpose: No DDL changes needed. EmployeeType is stored as NVARCHAR(20) via EF string conversion.
--          New value 'HourlyPartTime' is used for part-time hourly employees who do NOT receive
--          holiday pay. The EmployeeSyncService maps Entra groups TimeClock.Employee.StopSix.PT
--          and TimeClock.Employee.McCart.PT to this type.
--
-- To manually reclassify an existing employee as part-time:
-- UPDATE TC_Employees SET EmployeeType = 'HourlyPartTime', ModifiedDate = GETDATE()
-- WHERE EmployeeId = <id>;
--
-- To view all employee types in use:
-- SELECT EmployeeType, COUNT(*) FROM TC_Employees WHERE IsActive = 1 GROUP BY EmployeeType;

PRINT 'Migration 030: HourlyPartTime employee type documented. No schema changes required.';
