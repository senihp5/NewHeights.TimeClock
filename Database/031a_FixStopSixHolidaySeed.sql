-- Migration 031a: Fix Stop Six holiday seed after STOP6 -> STOPSIX campus code rename
-- Date: 2026-04-21
-- Related: 031_SeedHolidays_2025_2026.sql
-- Purpose: Migration 031 resolved the Stop Six campus via CampusCode = 'STOP6'.
--          After the 2026-04-20 rename of Attendance_Campuses.CampusCode to 'STOPSIX',
--          re-running 031 (or running it late) would leave @StopSixId = NULL, causing
--          the two Stop-Six-specific holidays (Labor Day 2025-09-01, Columbus Day
--          2025-10-13) to insert with CampusId = NULL + AppliesToAllCampuses = 0 —
--          an inconsistent "campus-specific but no campus" state.
--
-- This script is safe to run repeatedly — diagnostic + remediation are both idempotent.
-- Only rows carrying CreatedBy = 'SYSTEM_SEED' are touched by the cleanup step; any
-- manually created row with the same dates is preserved.
--
-- ═══════════════════════════════════════════════════════════════════
-- STEP 1: DIAGNOSTIC — run this block, read the output, decide
-- ═══════════════════════════════════════════════════════════════════

DECLARE @StopSixId INT = (SELECT CampusId FROM Attendance_Campuses WHERE CampusCode = 'STOPSIX');

PRINT '─────────────────────────────────────────────────────';
PRINT 'StopSix CampusId: ' + ISNULL(CAST(@StopSixId AS VARCHAR), 'NULL — abort')
PRINT '─────────────────────────────────────────────────────';

SELECT
    'Correctly tied to STOPSIX (2025-2026)' AS Metric,
    COUNT(*) AS Count
FROM TC_HolidaySchedule
WHERE CampusId = @StopSixId
  AND SchoolYear = '2025-2026'
  AND IsActive = 1
UNION ALL
SELECT
    'Orphaned campus-specific rows (CampusId NULL, AppliesToAll=0, SEED)',
    COUNT(*)
FROM TC_HolidaySchedule
WHERE CampusId IS NULL
  AND AppliesToAllCampuses = 0
  AND SchoolYear = '2025-2026'
  AND CreatedBy = 'SYSTEM_SEED'
UNION ALL
SELECT
    'Labor Day 2025-09-01 (any scope)',
    COUNT(*)
FROM TC_HolidaySchedule
WHERE HolidayDate = '2025-09-01' AND SchoolYear = '2025-2026'
UNION ALL
SELECT
    'Columbus Day 2025-10-13 (any scope)',
    COUNT(*)
FROM TC_HolidaySchedule
WHERE HolidayDate = '2025-10-13' AND SchoolYear = '2025-2026';

-- Detailed view of the two target dates so you can eyeball scope + CreatedBy
SELECT
    HolidayId,
    HolidayName,
    HolidayDate,
    CampusId,
    AppliesToAllCampuses,
    SchoolYear,
    IsActive,
    CreatedBy,
    CreatedDate
FROM TC_HolidaySchedule
WHERE HolidayDate IN ('2025-09-01', '2025-10-13')
ORDER BY HolidayDate, CampusId;

-- ═══════════════════════════════════════════════════════════════════
-- STEP 2: REMEDIATION — safe to run in all three outcomes below
-- ═══════════════════════════════════════════════════════════════════
-- Case A (clean):  Diagnostic shows 2 correctly-tied rows. Remediation is a no-op
--                  — the NOT EXISTS guards skip.
-- Case B (broken): Diagnostic shows 0 correctly-tied rows + 2 orphaned rows.
--                  DELETE clears the orphans, INSERTs add them with correct CampusId.
-- Case C (never seeded): Diagnostic shows 0 for both. DELETE is a no-op, INSERTs add.
-- ═══════════════════════════════════════════════════════════════════

IF @StopSixId IS NULL
BEGIN
    RAISERROR('No Attendance_Campuses row with CampusCode = ''STOPSIX''. Aborting remediation.', 16, 1);
    RETURN;
END

-- 2a: Clean orphaned campus-specific seed rows for the two target dates.
DELETE FROM TC_HolidaySchedule
WHERE CampusId IS NULL
  AND AppliesToAllCampuses = 0
  AND SchoolYear = '2025-2026'
  AND CreatedBy = 'SYSTEM_SEED'
  AND HolidayDate IN ('2025-09-01', '2025-10-13');

PRINT 'Orphaned rows removed: ' + CAST(@@ROWCOUNT AS VARCHAR);

-- 2b: Re-seed the two Stop-Six-specific holidays (idempotent).
IF NOT EXISTS (
    SELECT 1 FROM TC_HolidaySchedule
    WHERE HolidayDate = '2025-09-01' AND CampusId = @StopSixId
)
BEGIN
    INSERT INTO TC_HolidaySchedule
        (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses,
         SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate)
    VALUES
        ('Labor Day', '2025-09-01', 8.00, @StopSixId, 0,
         '2025-2026', 1, 'SYSTEM_SEED_031A_FIX', GETDATE(), GETDATE());
    PRINT 'Inserted: Labor Day 2025-09-01 (Stop Six)';
END
ELSE PRINT 'Skip: Labor Day 2025-09-01 already tied to STOPSIX.';

IF NOT EXISTS (
    SELECT 1 FROM TC_HolidaySchedule
    WHERE HolidayDate = '2025-10-13' AND CampusId = @StopSixId
)
BEGIN
    INSERT INTO TC_HolidaySchedule
        (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses,
         SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate)
    VALUES
        ('Columbus Day', '2025-10-13', 8.00, @StopSixId, 0,
         '2025-2026', 1, 'SYSTEM_SEED_031A_FIX', GETDATE(), GETDATE());
    PRINT 'Inserted: Columbus Day 2025-10-13 (Stop Six)';
END
ELSE PRINT 'Skip: Columbus Day 2025-10-13 already tied to STOPSIX.';

-- ═══════════════════════════════════════════════════════════════════
-- STEP 3: VERIFICATION — should return 2 Stop-Six-specific + shared totals
-- ═══════════════════════════════════════════════════════════════════

SELECT
    'Stop Six specific' AS Scope,
    COUNT(*) AS Rows,
    SUM(HoursCredited) AS Hours
FROM TC_HolidaySchedule
WHERE CampusId = @StopSixId
  AND SchoolYear = '2025-2026'
  AND IsActive = 1
UNION ALL
SELECT
    'Applies to all campuses',
    COUNT(*),
    SUM(HoursCredited)
FROM TC_HolidaySchedule
WHERE AppliesToAllCampuses = 1
  AND SchoolYear = '2025-2026'
  AND IsActive = 1;

PRINT 'Migration 031a: Stop Six holiday seed fix complete.';
