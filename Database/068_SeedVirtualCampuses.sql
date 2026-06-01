-- =====================================================================
-- Migration 068: Seed virtual campuses (Summer Term + Charter Waitlist)
--                + add IsVirtual column to Attendance_Campuses
-- =====================================================================
-- Purpose:
--   The CMS sync update (2026-05-28 handoff) revealed two PowerSchool
--   school IDs beyond the physical campuses we already model:
--     888 = Summer Term
--     777 = Charter School Waitlist
--
--   These are NOT physical campuses but they surface as distinct
--   SchoolID values in Advising_MasterSchedule. Class Attendance
--   materialization needs an Attendance_Campuses row to anchor to or
--   ClassRosterService.EnsureSectionCachedInternalAsync throws
--   InvalidOperationException for every summer / waitlist section.
--
--   New IsVirtual column distinguishes these from physical campuses
--   so consumers (StudentCheckin, MobileCheckin, KioskScanService)
--   can exclude them from physical-presence pickers the same way
--   AppConstants.Campus.DistrictCode excludes the District Office row.
--
-- Rollback:
--   DELETE FROM [dbo].[Attendance_Campuses]
--    WHERE PowerSchoolId IN (888, 777) AND IsVirtual = 1;
--   ALTER TABLE [dbo].[Attendance_Campuses] DROP COLUMN [IsVirtual];
-- =====================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE name = 'IsVirtual'
      AND object_id = OBJECT_ID('[dbo].[Attendance_Campuses]')
)
BEGIN
    ALTER TABLE [dbo].[Attendance_Campuses]
        ADD [IsVirtual] BIT NOT NULL DEFAULT 0;

    PRINT 'Added IsVirtual column to Attendance_Campuses';
END
ELSE
BEGIN
    PRINT 'Attendance_Campuses.IsVirtual already exists - skipping ADD';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM [dbo].[Attendance_Campuses]
    WHERE PowerSchoolId = 888
)
BEGIN
    INSERT INTO [dbo].[Attendance_Campuses]
        ([CampusCode], [CampusName], [PowerSchoolId], [DistrictId],
         [IsVirtual], [SchoolNameValue])
    VALUES
        ('SUMMER', 'Summer Term', 888, 1, 1, 'Summer Term');

    PRINT 'Seeded Summer Term virtual campus (PowerSchoolId=888)';
END
ELSE
BEGIN
    PRINT 'Summer Term campus already seeded - skipping';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM [dbo].[Attendance_Campuses]
    WHERE PowerSchoolId = 777
)
BEGIN
    INSERT INTO [dbo].[Attendance_Campuses]
        ([CampusCode], [CampusName], [PowerSchoolId], [DistrictId],
         [IsVirtual], [SchoolNameValue])
    VALUES
        ('WAITLIST', 'Charter School Waitlist', 777, 1, 1, 'Charter School Waitlist');

    PRINT 'Seeded Charter School Waitlist virtual campus (PowerSchoolId=777)';
END
ELSE
BEGIN
    PRINT 'Charter School Waitlist campus already seeded - skipping';
END
GO

-- =====================================================================
-- Verification
-- =====================================================================
SELECT CampusId, CampusCode, CampusName, PowerSchoolId, DistrictId, IsVirtual
FROM [dbo].[Attendance_Campuses]
ORDER BY IsVirtual, CampusId;
