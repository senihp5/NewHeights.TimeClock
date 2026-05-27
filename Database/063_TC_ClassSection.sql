-- =====================================================================
-- Migration 063: Create TC_ClassSection
-- =====================================================================
-- Purpose:
--   Canonical class-section identity, keyed to PowerSchool's
--   sections.id (the URL parameter on PS's /teachers/attendance-grid.action
--   page). Holds the resolved teacher of record, course, period, room,
--   and term for every section students attend.
--
--   Lazy-materialized by IClassRosterService on first reference: when
--   a teacher opens /teacher/today or a student scans a room QR, the
--   service reads CaseManagementDB.dbo.Advising_StudentSchedule
--   (cross-DB) for the row, then upserts a TC_ClassSection row here
--   so subsequent loads avoid the cross-DB hop.
--
--   LastSyncDate lets a background refresh task (Phase B+) update
--   the row when course or teacher changes upstream in PS.
--
-- Relationships:
--   - DistrictId  -> TC_District            (denormalized for district reports)
--   - CampusId    -> Attendance_Campuses    (denormalized for receptionist queries)
--   - MasterScheduleId -> TC_MasterSchedule (optional cross-ref to our own import)
--   - TeacherDcid is a soft reference to Staff.Dcid (no FK because the
--     Staff table is PS-sourced and rows can disappear on a sync).
--
-- Anchors:
--   - PS sections.id is BIGINT in PowerSchool (Oracle NUMBER). We store
--     as BIGINT here to avoid future precision loss; the URL parameter
--     observed in the test data was 1901 but production sections can
--     have larger IDs.
--   - SchoolYear is NVARCHAR(9) to match the legacy TC_MasterSchedule
--     column, but the canonical stored format is the short 6-char form
--     ('2025-26'). See reference_master_schedule_format memory.
--
-- Rollback:
--   DROP TABLE [dbo].[TC_ClassSection];
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_ClassSection')
BEGIN
    CREATE TABLE [dbo].[TC_ClassSection]
    (
        [ClassSectionId]     INT             IDENTITY(1, 1) NOT NULL,
        [PsSectionId]        BIGINT          NOT NULL,
        [DistrictId]         INT             NOT NULL DEFAULT 1,
        [CampusId]           INT             NOT NULL,
        [PsSchoolId]         BIGINT          NULL,
        [CourseNumber]       NVARCHAR(50)    NOT NULL,
        [CourseName]         NVARCHAR(200)   NOT NULL,
        [SectionNumber]      NVARCHAR(20)    NULL,
        [Expression]         NVARCHAR(20)    NULL,
        [PeriodNumber]       INT             NULL,
        [Room]               NVARCHAR(50)    NULL,
        [TeacherDcid]        INT             NULL,
        [TermName]           NVARCHAR(20)    NULL,
        [SchoolYear]         NVARCHAR(9)     NULL,
        [TermId]             INT             NULL,
        [MasterScheduleId]   INT             NULL,
        [IsActive]           BIT             NOT NULL DEFAULT 1,
        [LastSyncDate]       DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [CreatedDate]        DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [ModifiedDate]       DATETIME2       NULL,
        CONSTRAINT [PK_TC_ClassSection]
            PRIMARY KEY CLUSTERED ([ClassSectionId] ASC),
        CONSTRAINT [UQ_TC_ClassSection_PsSectionId]
            UNIQUE NONCLUSTERED ([PsSectionId]),
        CONSTRAINT [FK_TC_ClassSection_District]
            FOREIGN KEY ([DistrictId]) REFERENCES [dbo].[TC_District]([DistrictId]),
        CONSTRAINT [FK_TC_ClassSection_Campus]
            FOREIGN KEY ([CampusId]) REFERENCES [dbo].[Attendance_Campuses]([CampusId]),
        CONSTRAINT [FK_TC_ClassSection_MasterSchedule]
            FOREIGN KEY ([MasterScheduleId]) REFERENCES [dbo].[TC_MasterSchedule]([ScheduleId])
    );

    CREATE NONCLUSTERED INDEX [IX_TC_ClassSection_CampusTerm]
        ON [dbo].[TC_ClassSection] ([CampusId], [TermName], [SchoolYear])
        INCLUDE ([CourseName], [PeriodNumber], [TeacherDcid], [Room])
        WHERE [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_ClassSection_TeacherDcid]
        ON [dbo].[TC_ClassSection] ([TeacherDcid])
        INCLUDE ([CampusId], [CourseName], [PeriodNumber], [Room], [TermName], [SchoolYear])
        WHERE [IsActive] = 1 AND [TeacherDcid] IS NOT NULL;

    CREATE NONCLUSTERED INDEX [IX_TC_ClassSection_District_IsActive]
        ON [dbo].[TC_ClassSection] ([DistrictId], [IsActive])
        INCLUDE ([CampusId], [TermName]);

    CREATE NONCLUSTERED INDEX [IX_TC_ClassSection_LastSyncDate]
        ON [dbo].[TC_ClassSection] ([LastSyncDate])
        WHERE [IsActive] = 1;

    PRINT 'Created TC_ClassSection table';
END
ELSE
BEGIN
    PRINT 'TC_ClassSection table already exists - skipping create';
END
GO

-- =====================================================================
-- Verification (table is empty until IClassRosterService starts
-- materializing sections in Phase B)
-- =====================================================================
SELECT
    OBJECT_NAME(parent_object_id) AS TableName,
    name AS ConstraintName,
    type_desc AS ConstraintType
FROM sys.objects
WHERE parent_object_id = OBJECT_ID('[dbo].[TC_ClassSection]')
  AND type IN ('PK', 'UQ', 'F')
ORDER BY type_desc, name;

SELECT
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.has_filter AS HasFilter,
    i.filter_definition AS FilterDef
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('[dbo].[TC_ClassSection]')
  AND i.type > 0
ORDER BY i.name;
