-- =====================================================================
-- Migration 066: Create TC_ClassObservation
-- =====================================================================
-- Purpose:
--   Internal teacher journal - one row per observation, per
--   (student, section, date). Multiple rows per period allowed:
--   e.g. "arrived 9:12 limping" + "asked to go to nurse 9:30" +
--   "returned with bandage 9:55" all on the same date for the
--   same student.
--
--   NEVER transcribed to PowerSchool. This table is purely for the
--   district's own behavioral / wellness / academic concern tracking.
--   Sits alongside TC_ClassAttendance (per-cell PS-bound state) so
--   teachers and reception have a richer record than the four-code
--   PS dropdown can express.
--
-- Soft-delete via IsActive:
--   Observations can be redacted but never hard-deleted - audit
--   trail of what was originally written is preserved. The teacher
--   who wrote it (or CampusAdmin+) can flip IsActive=0 if the
--   entry was made in error.
--
-- Category enum (HasConversion<string>()):
--   Behavior | Health | Academic | Other
--
-- Rollback:
--   DROP TABLE [dbo].[TC_ClassObservation];
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_ClassObservation')
BEGIN
    CREATE TABLE [dbo].[TC_ClassObservation]
    (
        [ObservationId]         BIGINT          IDENTITY(1, 1) NOT NULL,
        [ClassSectionId]        INT             NOT NULL,
        [StudentDcid]           INT             NOT NULL,
        [StudentNumber]         NVARCHAR(50)    NOT NULL,
        [StudentLastName]       NVARCHAR(100)   NULL,
        [StudentFirstName]      NVARCHAR(100)   NULL,
        [ObservationDate]       DATE            NOT NULL,
        [ObservationDateTime]   DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [DistrictId]            INT             NOT NULL DEFAULT 1,
        [CampusId]              INT             NOT NULL,
        [Category]              NVARCHAR(20)    NOT NULL,
        [ObservationText]       NVARCHAR(MAX)   NOT NULL,
        [AuthorEmail]           NVARCHAR(256)   NOT NULL,
        [IsActive]              BIT             NOT NULL DEFAULT 1,
        [CreatedDate]           DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [ModifiedDate]          DATETIME2       NULL,
        CONSTRAINT [PK_TC_ClassObservation]
            PRIMARY KEY CLUSTERED ([ObservationId] ASC),
        CONSTRAINT [FK_TC_ClassObservation_ClassSection]
            FOREIGN KEY ([ClassSectionId]) REFERENCES [dbo].[TC_ClassSection]([ClassSectionId]),
        CONSTRAINT [FK_TC_ClassObservation_District]
            FOREIGN KEY ([DistrictId]) REFERENCES [dbo].[TC_District]([DistrictId]),
        CONSTRAINT [FK_TC_ClassObservation_Campus]
            FOREIGN KEY ([CampusId]) REFERENCES [dbo].[Attendance_Campuses]([CampusId]),
        CONSTRAINT [CK_TC_ClassObservation_Category]
            CHECK ([Category] IN ('Behavior', 'Health', 'Academic', 'Other'))
    );

    CREATE NONCLUSTERED INDEX [IX_TC_ClassObservation_Student_Date]
        ON [dbo].[TC_ClassObservation] ([StudentDcid], [ObservationDate])
        INCLUDE ([ClassSectionId], [Category], [ObservationDateTime], [AuthorEmail])
        WHERE [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_ClassObservation_Section_Date]
        ON [dbo].[TC_ClassObservation] ([ClassSectionId], [ObservationDate])
        INCLUDE ([StudentDcid], [StudentNumber], [Category], [ObservationDateTime], [AuthorEmail])
        WHERE [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_ClassObservation_Campus_Date]
        ON [dbo].[TC_ClassObservation] ([CampusId], [ObservationDate])
        INCLUDE ([StudentDcid], [Category])
        WHERE [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_ClassObservation_District_Date]
        ON [dbo].[TC_ClassObservation] ([DistrictId], [ObservationDate])
        WHERE [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_ClassObservation_Category_Date]
        ON [dbo].[TC_ClassObservation] ([Category], [ObservationDate])
        INCLUDE ([CampusId], [StudentDcid])
        WHERE [IsActive] = 1;

    PRINT 'Created TC_ClassObservation table';
END
ELSE
BEGIN
    PRINT 'TC_ClassObservation table already exists - skipping create';
END
GO

-- =====================================================================
-- Verification (table is empty until Phase C observations panel writes)
-- =====================================================================
SELECT
    name AS ConstraintName,
    type_desc AS ConstraintType
FROM sys.objects
WHERE parent_object_id = OBJECT_ID('[dbo].[TC_ClassObservation]')
  AND type IN ('PK', 'UQ', 'F', 'C')
ORDER BY type_desc, name;

SELECT
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.has_filter AS HasFilter,
    i.filter_definition AS FilterDef
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('[dbo].[TC_ClassObservation]')
  AND i.type > 0
ORDER BY i.name;
