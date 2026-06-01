-- =====================================================================
-- Migration 068a: Retry virtual campus INSERTs with SchoolNameValue
-- =====================================================================
-- Purpose:
--   Migration 068's two INSERT statements failed with Msg 515 because
--   Attendance_Campuses.SchoolNameValue is NOT NULL in the actual DB
--   schema. The Campus entity declares it as 'string?' (nullable),
--   which is a model-vs-schema drift - the entity is wrong, the
--   column constraint is right.
--
--   The ALTER TABLE in 068 succeeded; IsVirtual column exists.
--   Only the two seed INSERTs need to be retried with
--   SchoolNameValue populated.
--
-- Rollback:
--   DELETE FROM [dbo].[Attendance_Campuses]
--    WHERE PowerSchoolId IN (888, 777) AND IsVirtual = 1;
-- =====================================================================

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
SELECT CampusId, CampusCode, CampusName, PowerSchoolId, DistrictId, IsVirtual, SchoolNameValue
FROM [dbo].[Attendance_Campuses]
ORDER BY IsVirtual, CampusId;
