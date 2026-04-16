-- Migration 031: Seed 2025-2026 Holiday Schedule from campus academic calendars
-- Date: 2026-04-16
-- Source: AnnualCalendarMcCart.pdf and AnnualCalendarStopSix.pdf
-- Purpose: Populate TC_HolidaySchedule with all holiday/break/closure dates
--          that credit 8 hours for full-time hourly employees.
-- Note: Weekends are excluded. Only weekday dates are seeded.

-- Get campus IDs
DECLARE @StopSixId INT = (SELECT CampusId FROM Attendance_Campuses WHERE CampusCode = 'STOP6');
DECLARE @McCartId  INT = (SELECT CampusId FROM Attendance_Campuses WHERE CampusCode = 'MCCART');

-- ═══════════════════════════════════════════════════════════════════
-- STOP SIX ONLY holidays (campus-specific)
-- ═══════════════════════════════════════════════════════════════════

-- Sep 1, 2025 - Labor Day (Stop Six only, McCart not in session yet)
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2025-09-01' AND CampusId = @StopSixId)
INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate)
VALUES ('Labor Day', '2025-09-01', 8.00, @StopSixId, 0, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());

-- Oct 13, 2025 - Columbus Day (Stop Six only, McCart not in session yet)
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2025-10-13' AND CampusId = @StopSixId)
INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate)
VALUES ('Columbus Day', '2025-10-13', 8.00, @StopSixId, 0, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());

-- ═══════════════════════════════════════════════════════════════════
-- ALL CAMPUSES holidays (shared dates)
-- ═══════════════════════════════════════════════════════════════════

-- Nov 24-28, 2025 - Fall Break (Mon-Fri)
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2025-11-24' AND AppliesToAllCampuses = 1)
BEGIN
    INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate) VALUES
    ('Fall Break', '2025-11-24', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Fall Break', '2025-11-25', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Fall Break - Thanksgiving', '2025-11-26', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Thanksgiving Day', '2025-11-27', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Fall Break', '2025-11-28', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());
END

-- Dec 22, 2025 - Jan 7, 2026 - Winter Break (weekdays only)
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2025-12-22' AND AppliesToAllCampuses = 1)
BEGIN
    INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate) VALUES
    ('Winter Break', '2025-12-22', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Winter Break', '2025-12-23', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Christmas Eve', '2025-12-24', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Christmas Day', '2025-12-25', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Winter Break', '2025-12-26', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Winter Break', '2025-12-29', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Winter Break', '2025-12-30', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('New Year''s Eve', '2025-12-31', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('New Year''s Day', '2026-01-01', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Winter Break', '2026-01-02', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Winter Break', '2026-01-05', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Winter Break', '2026-01-06', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Winter Break', '2026-01-07', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());
END

-- Jan 19, 2026 - MLK Day
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2026-01-19' AND AppliesToAllCampuses = 1)
INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate)
VALUES ('Martin Luther King Jr. Day', '2026-01-19', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());

-- Feb 13, 2026 (Fri) and Feb 16, 2026 (Mon) - Presidents Day weekend
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2026-02-13' AND AppliesToAllCampuses = 1)
BEGIN
    INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate) VALUES
    ('Presidents'' Day Break', '2026-02-13', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Presidents'' Day', '2026-02-16', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());
END

-- Mar 16-20, 2026 - Spring Break (Mon-Fri)
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2026-03-16' AND AppliesToAllCampuses = 1)
BEGIN
    INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate) VALUES
    ('Spring Break', '2026-03-16', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Spring Break', '2026-03-17', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Spring Break', '2026-03-18', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Spring Break', '2026-03-19', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('Spring Break', '2026-03-20', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());
END

-- Apr 3, 2026 - Good Friday
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2026-04-03' AND AppliesToAllCampuses = 1)
INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate)
VALUES ('Good Friday', '2026-04-03', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());

-- May 15, 2026 - School/District Holiday
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2026-05-15' AND AppliesToAllCampuses = 1)
INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate)
VALUES ('School Holiday', '2026-05-15', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());

-- May 25, 2026 - Memorial Day
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2026-05-25' AND AppliesToAllCampuses = 1)
INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate)
VALUES ('Memorial Day', '2026-05-25', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());

-- Jun 26 - Jul 6, 2026 - District Closed (weekdays only)
-- Jun 26 Fri, Jun 29 Mon, Jun 30 Tue, Jul 1 Wed, Jul 2 Thu, Jul 3 Fri (Jul 6 Mon)
IF NOT EXISTS (SELECT 1 FROM TC_HolidaySchedule WHERE HolidayDate = '2026-06-26' AND AppliesToAllCampuses = 1)
BEGIN
    INSERT INTO TC_HolidaySchedule (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses, SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate) VALUES
    ('District Closed', '2026-06-26', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('District Closed', '2026-06-29', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('District Closed', '2026-06-30', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('District Closed', '2026-07-01', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('District Closed', '2026-07-02', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('District Closed - Independence Day Eve', '2026-07-03', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE()),
    ('District Closed', '2026-07-06', 8.00, NULL, 1, '2025-2026', 1, 'SYSTEM_SEED', GETDATE(), GETDATE());
END

-- Summary
SELECT
    COUNT(*) AS TotalHolidays,
    SUM(HoursCredited) AS TotalHoursCredited,
    SUM(CASE WHEN AppliesToAllCampuses = 1 THEN 1 ELSE 0 END) AS AllCampusDays,
    SUM(CASE WHEN AppliesToAllCampuses = 0 THEN 1 ELSE 0 END) AS CampusSpecificDays
FROM TC_HolidaySchedule
WHERE SchoolYear = '2025-2026' AND IsActive = 1;

PRINT 'Migration 031: 2025-2026 holidays seeded successfully.';
