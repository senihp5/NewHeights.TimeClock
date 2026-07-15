-- =====================================================================
-- Migration 064: Create TC_SectionEnrollment
-- =====================================================================
-- Purpose:
--   Per-(student, section) enrollment rows. Local mirror of the
--   PowerSchool [cc] (course-class) table - one row per enrollment
--   span, with DateEnrolled and DateLeft bracketing each span.
--
--   When a student withdraws and re-enrolls in the same section,
--   PS creates a new cc row; we follow the same convention so the
--   enrollment history matches PS exactly. Active enrollment is
--   any row where DateLeft IS NULL.
--
--   Materialized by IClassRosterService (Phase B) on the same lazy
--   path as TC_ClassSection: read from CMS Advising_StudentSchedule
--   (cross-DB) on first reference, upsert here, then serve roster
--   queries from local cache.
--
-- Denormalization rationale:
--   - StudentNumber: indexed for badge-scan lookups (the QR / NFC
--     payload carries the student number, NOT the DCID).
--   - StudentLastName / StudentFirstName: avoids a JOIN to Students
--     on every roster render. Receptionist daily view loads under 10ms
--     even with 100+ active sections.
--   - DistrictId / CampusId: avoids a JOIN to TC_ClassSection ->
--     Attendance_Campuses on every district/campus report query.
--
-- Relationships:
--   - ClassSectionId -> TC_ClassSection (hard FK)
--   - DistrictId     -> TC_District    (hard FK)
--   - CampusId       -> Attendance_Campuses (hard FK)
--   - StudentDcid    -> Students.Dcid  (soft reference, no FK
--                       because Students is PS-sourced and rows
--                       can disappear on a sync)
--
-- Rollback:
--   DROP TABLE [dbo].[TC_SectionEnrollment];
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_SectionEnrollment')
BEGIN
    CREATE TABLE [dbo].[TC_SectionEnrollment]
    (
        [EnrollmentId]       INT             IDENTITY(1, 1) NOT NULL,
        [ClassSectionId]     INT             NOT NULL,
        [StudentDcid]        INT             NOT NULL,
        [StudentNumber]      NVARCHAR(50)    NOT NULL,
        [StudentLastName]    NVARCHAR(100)   NULL,
        [StudentFirstName]   NVARCHAR(100)   NULL,
        [DistrictId]         INT             NOT NULL DEFAULT 1,
        [CampusId]           INT             NOT NULL,
        [DateEnrolled]       DATE            NULL,
        [DateLeft]           DATE            NULL,
        [IsActive]           BIT             NOT NULL DEFAULT 1,
        [LastSyncDate]       DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [CreatedDate]        DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [ModifiedDate]       DATETIME2       NULL,
        CONSTRAINT [PK_TC_SectionEnrollment]
            PRIMARY KEY CLUSTERED ([EnrollmentId] ASC),
        CONSTRAINT [FK_TC_SectionEnrollment_ClassSection]
            FOREIGN KEY ([ClassSectionId]) REFERENCES [dbo].[TC_ClassSection]([ClassSectionId]),
        CONSTRAINT [FK_TC_SectionEnrollment_District]
            FOREIGN KEY ([DistrictId]) REFERENCES [dbo].[TC_District]([DistrictId]),
        CONSTRAINT [FK_TC_SectionEnrollment_Campus]
            FOREIGN KEY ([CampusId]) REFERENCES [dbo].[Attendance_Campuses]([CampusId])
    );

    CREATE UNIQUE NONCLUSTERED INDEX [UQ_TC_SectionEnrollment_ActiveSpan]
        ON [dbo].[TC_SectionEnrollment] ([ClassSectionId], [StudentDcid])
        WHERE [DateLeft] IS NULL AND [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_SectionEnrollment_StudentDcid]
        ON [dbo].[TC_SectionEnrollment] ([StudentDcid])
        INCLUDE ([ClassSectionId], [DateEnrolled], [DateLeft], [CampusId])
        WHERE [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_SectionEnrollment_StudentNumber]
        ON [dbo].[TC_SectionEnrollment] ([StudentNumber])
        INCLUDE ([StudentDcid], [ClassSectionId], [DateEnrolled], [DateLeft])
        WHERE [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_SectionEnrollment_ClassSection_Active]
        ON [dbo].[TC_SectionEnrollment] ([ClassSectionId])
        INCLUDE ([StudentDcid], [StudentNumber], [StudentLastName], [StudentFirstName])
        WHERE [DateLeft] IS NULL AND [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_SectionEnrollment_CampusId]
        ON [dbo].[TC_SectionEnrollment] ([CampusId], [IsActive]);

    CREATE NONCLUSTERED INDEX [IX_TC_SectionEnrollment_DistrictId]
        ON [dbo].[TC_SectionEnrollment] ([DistrictId], [IsActive]);

    PRINT 'Created TC_SectionEnrollment table';
END
ELSE
BEGIN
    PRINT 'TC_SectionEnrollment table already exists - skipping create';
END
GO

-- =====================================================================
-- Verification (table is empty until IClassRosterService starts
-- materializing enrollments in Phase B)
-- =====================================================================
SELECT
    name AS ConstraintName,
    type_desc AS ConstraintType
FROM sys.objects
WHERE parent_object_id = OBJECT_ID('[dbo].[TC_SectionEnrollment]')
  AND type IN ('PK', 'UQ', 'F')
ORDER BY type_desc, name;

SELECT
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    i.has_filter AS HasFilter,
    i.filter_definition AS FilterDef
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('[dbo].[TC_SectionEnrollment]')
  AND i.type > 0
ORDER BY i.name;
