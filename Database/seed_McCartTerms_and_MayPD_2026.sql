/*
===============================================================================
Seed: McCart term-label correction + May 25-29 End-of-Year PD week
Date: 2026-04-22
Source: Patrick 2026-04-22 correction
  - McCart's master-schedule rows were loaded labeled one term behind because
    classes didn't start at the McCart building until 2025-10-20. The term
    DATES are authoritative in CaseManagementDB.dbo.Advising_TermConfig and
    match Stop Six exactly — we only need to relabel McCart's local master
    schedule rows: T1→T2, T2→T3, T3→T4.
  - Both campuses are missing an End-of-Year Professional Development week
    May 25-29 2026. Added as non-instructional (blocks sub requests) but
    staff is expected to clock in normally → HoursCredited = 0.

Idempotent: re-runnable without duplicating rows or double-shifting terms.
===============================================================================
*/
SET NOCOUNT ON;
GO

DECLARE @Year NVARCHAR(20) = '2025-2026';
DECLARE @SeedUser NVARCHAR(100) = 'seed:mccart-terms-may-pd-2026-04-22';

DECLARE @McCart_Id   INT = (SELECT TOP 1 CampusId FROM Attendance_Campuses
                            WHERE CampusName LIKE '%McCart%' AND CampusCode <> 'DISTRICT'
                            ORDER BY CampusId);
DECLARE @StopSix_Id  INT = (SELECT TOP 1 CampusId FROM Attendance_Campuses
                            WHERE (CampusName LIKE '%Stop Six%' OR CampusCode = 'STOPSIX')
                              AND CampusCode <> 'DISTRICT'
                            ORDER BY CampusId);

IF @McCart_Id IS NULL OR @StopSix_Id IS NULL
BEGIN
    RAISERROR('Could not resolve both campuses. Aborting.', 16, 1);
    RETURN;
END

PRINT 'Resolved campuses:';
PRINT '  McCart   = ' + CAST(@McCart_Id AS NVARCHAR(10));
PRINT '  Stop Six = ' + CAST(@StopSix_Id AS NVARCHAR(10));
PRINT '';

-------------------------------------------------------------------------------
-- SECTION 1: McCart term rename (T1 → T2, T2 → T3, T3 → T4)
--
-- Safety:
--  * Hard-fail if McCart already has any row labeled T4 — that would indicate
--    the rename was previously applied (idempotency guard) OR the data is
--    mixed, and either way we don't want to silently double-shift.
--  * Single-statement CASE rename is safe because SQL Server evaluates the
--    expression against pre-update values for ALL rows before applying any
--    change. T1 rows become T2 and T2 rows become T3 in the same pass — the
--    already-shifted T2 rows are not re-processed.
-------------------------------------------------------------------------------
PRINT '>>> Section 1: McCart term rename';

DECLARE @McCartT4Count INT = (
    SELECT COUNT(*) FROM TC_MasterSchedule
    WHERE CampusId = @McCart_Id AND TermName = 'T4' AND SchoolYear = @Year
);
DECLARE @McCartT1T3Count INT = (
    SELECT COUNT(*) FROM TC_MasterSchedule
    WHERE CampusId = @McCart_Id AND TermName IN ('T1','T2','T3') AND SchoolYear = @Year
);

IF @McCartT4Count > 0 AND @McCartT1T3Count = 0
BEGIN
    PRINT '    skipped — McCart already has T4 rows and no T1/T2/T3 rows; rename appears complete.';
END
ELSE IF @McCartT4Count > 0 AND @McCartT1T3Count > 0
BEGIN
    PRINT '    ! MIXED STATE — McCart has both T4 rows AND T1/T2/T3 rows.';
    PRINT '    Counts before rename:';
    SELECT TermName, COUNT(*) AS Rows
    FROM TC_MasterSchedule
    WHERE CampusId = @McCart_Id AND SchoolYear = @Year
    GROUP BY TermName
    ORDER BY TermName;
    RAISERROR('Mixed term state on McCart — review and resolve manually before re-running.', 16, 1);
    RETURN;
END
ELSE
BEGIN
    PRINT '    Counts before rename:';
    SELECT TermName, COUNT(*) AS Rows
    FROM TC_MasterSchedule
    WHERE CampusId = @McCart_Id AND SchoolYear = @Year
    GROUP BY TermName
    ORDER BY TermName;

    UPDATE TC_MasterSchedule
    SET TermName = CASE TermName
                      WHEN 'T1' THEN 'T2'
                      WHEN 'T2' THEN 'T3'
                      WHEN 'T3' THEN 'T4'
                      ELSE TermName
                   END,
        Notes = ISNULL(Notes, '') + ' [Term label shifted +1 on ' + CONVERT(VARCHAR, GETDATE(), 120) + ' per McCart building-occupancy correction]'
    WHERE CampusId = @McCart_Id
      AND SchoolYear = @Year
      AND TermName IN ('T1','T2','T3');

    DECLARE @Renamed INT = @@ROWCOUNT;
    PRINT '    renamed ' + CAST(@Renamed AS NVARCHAR(10)) + ' master schedule row(s).';

    PRINT '    Counts after rename:';
    SELECT TermName, COUNT(*) AS Rows
    FROM TC_MasterSchedule
    WHERE CampusId = @McCart_Id AND SchoolYear = @Year
    GROUP BY TermName
    ORDER BY TermName;
END
PRINT '';

-------------------------------------------------------------------------------
-- SECTION 2: End-of-Year PD week — May 25-29 2026
--
-- One row per campus per day (5 rows × 2 campuses = 10 rows). AppliesToAllCampuses
-- = 0 so each campus has its own row (easier to disable per campus later if
-- schedules diverge). HoursCredited = 0 because staff still clocks in for PD;
-- the row exists to flag these dates as non-instructional (sub requests blocked,
-- calendar view flags the day).
-------------------------------------------------------------------------------
PRINT '>>> Section 2: May 25-29 End-of-Year PD';

DECLARE @PdDates TABLE (PdDate DATE, PdName NVARCHAR(100));
INSERT INTO @PdDates VALUES
    ('2026-05-25', 'End-of-Year PD — Mon May 25'),
    ('2026-05-26', 'End-of-Year PD — Tue May 26'),
    ('2026-05-27', 'End-of-Year PD — Wed May 27'),
    ('2026-05-28', 'End-of-Year PD — Thu May 28'),
    ('2026-05-29', 'End-of-Year PD — Fri May 29');

DECLARE @Campuses TABLE (CampusId INT, CampusLabel NVARCHAR(50));
INSERT INTO @Campuses VALUES
    (@McCart_Id, 'McCart'),
    (@StopSix_Id, 'Stop Six');

DECLARE @PdInserts INT = 0;

INSERT INTO TC_HolidaySchedule
    (HolidayName, HolidayDate, HoursCredited, CampusId, AppliesToAllCampuses,
     SchoolYear, IsActive, CreatedBy, CreatedDate, ModifiedDate)
SELECT
    d.PdName,
    CAST(d.PdDate AS DATE),
    0.0,                        -- staff clocks in for PD; no auto-credit
    c.CampusId,
    0,                          -- campus-specific (not all-campuses)
    @Year,
    1,
    @SeedUser,
    GETDATE(),
    GETDATE()
FROM @PdDates d
CROSS JOIN @Campuses c
WHERE NOT EXISTS (
    SELECT 1 FROM TC_HolidaySchedule h
    WHERE h.HolidayDate = CAST(d.PdDate AS DATE)
      AND h.CampusId = c.CampusId
      AND h.SchoolYear = @Year
      AND h.IsActive = 1
);

SET @PdInserts = @@ROWCOUNT;
PRINT '    inserted ' + CAST(@PdInserts AS NVARCHAR(10)) + ' PD-day row(s).';
PRINT '';

-------------------------------------------------------------------------------
-- VERIFICATION
-------------------------------------------------------------------------------
PRINT '=== Verification: McCart term counts ===';
SELECT TermName, COUNT(*) AS MasterScheduleRows
FROM TC_MasterSchedule
WHERE CampusId = @McCart_Id AND SchoolYear = @Year AND IsActive = 1
GROUP BY TermName
ORDER BY TermName;

PRINT '';
PRINT '=== Verification: May PD rows ===';
SELECT c.CampusName, h.HolidayDate, h.HolidayName, h.HoursCredited, h.IsActive
FROM TC_HolidaySchedule h
JOIN Attendance_Campuses c ON c.CampusId = h.CampusId
WHERE h.HolidayDate BETWEEN '2026-05-25' AND '2026-05-29'
  AND h.SchoolYear = @Year
ORDER BY c.CampusName, h.HolidayDate;
GO
