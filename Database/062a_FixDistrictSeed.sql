-- =====================================================================
-- Migration 062a: Correct TC_District seed - district is "New Heights Texas"
-- =====================================================================
-- Purpose:
--   Migration 062 seeded DistrictId 1 as DistrictCode='NHAHS' /
--   DistrictName='New Heights Adult High School', conflating the
--   SCHOOL name with the DISTRICT name. The actual hierarchy is:
--
--     District: New Heights Texas  (NHTX)            <-- this table
--     School:   New Heights Adult High School        <-- not modeled separately
--     Campuses: Stop Six (STOPSIX), McCart (MCCART)  <-- Attendance_Campuses
--
--   The "DISTRICT" row in Attendance_Campuses is a logical anchor
--   for district-office staff (no physical school location), not a
--   third class-attendance campus.
--
--   Follows the same corrective pattern as 031a_FixStopSixHolidaySeed
--   (fix a prior seed without rewriting historical migration files).
--
-- Rollback:
--   UPDATE [dbo].[TC_District]
--      SET [DistrictCode] = 'NHAHS',
--          [DistrictName] = 'New Heights Adult High School',
--          [Notes]        = 'Initial seed - sole district at launch'
--    WHERE [DistrictId] = 1;
-- =====================================================================

IF EXISTS (
    SELECT 1 FROM [dbo].[TC_District]
    WHERE [DistrictId] = 1 AND [DistrictCode] = 'NHAHS'
)
BEGIN
    UPDATE [dbo].[TC_District]
       SET [DistrictCode] = 'NHTX',
           [DistrictName] = 'New Heights Texas',
           [Notes]        = 'Corrected by 062a - NHAHS is the school name, not the district',
           [ModifiedDate] = SYSDATETIME()
     WHERE [DistrictId] = 1;

    PRINT 'Updated DistrictId 1: NHAHS -> NHTX (New Heights Texas)';
END
ELSE
BEGIN
    PRINT 'TC_District row already corrected or seed missing - skipping';
END
GO

SELECT DistrictId, DistrictCode, DistrictName, TimeZone, IsActive, Notes, ModifiedDate
FROM [dbo].[TC_District]
ORDER BY DistrictId;
