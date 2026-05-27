-- =====================================================================
-- Migration 065: Create TC_ClassAttendance
-- =====================================================================
-- Purpose:
--   The cell-level grain table - one row per (student, section, date).
--   Holds the actual attendance state the teacher records and the
--   receptionist reconciles against PowerSchool.
--
-- Status enum (HasConversion<string>() in EF):
--   Present  - default; transcribes to PS '' (blank)
--   Tardy    - student arrived after TcBellPeriod.TardyThresholdMinutes;
--              transcribes to PS 'T'
--   Absent   - student not present; transcribes to PS 'UA'
--   Excused  - student absent with reason; transcribes to PS 'UA' with
--              the excuse in Comment (PS dropdown has no E code at NH)
--   EarlyOut - student left before period end; transcribes to PS ''
--              (no PS code) with a note on the archive PDF
--
-- Source enum (HasConversion<string>()):
--   TeacherManual / QrPhone / ClassroomKiosk / Inferred / BulkPresent
--
-- PsAttendanceCode is a PERSISTED computed column so receptionist
-- queries that filter on the PS code don't have to evaluate the
-- CASE on every read. Saves milliseconds at scale.
--
-- Indexes are designed for the four real access patterns:
--   1. Teacher loads roster grid for (section, date)
--   2. Receptionist loads "all absences today" for a campus
--   3. Reports load per-student attendance over a date range
--   4. District/campus aggregate counts by date
--
-- No FK to Students - PS-sourced, rows can disappear on sync.
-- Soft reference to TcClassSection via FK (sections are TC-managed).
--
-- Rollback:
--   DROP TABLE [dbo].[TC_ClassAttendance];
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_ClassAttendance')
BEGIN
    CREATE TABLE [dbo].[TC_ClassAttendance]
    (
        [AttendanceId]       BIGINT          IDENTITY(1, 1) NOT NULL,
        [ClassSectionId]     INT             NOT NULL,
        [StudentDcid]        INT             NOT NULL,
        [StudentNumber]      NVARCHAR(50)    NOT NULL,
        [AttendanceDate]     DATE            NOT NULL,
        [DistrictId]         INT             NOT NULL DEFAULT 1,
        [CampusId]           INT             NOT NULL,
        [Status]             NVARCHAR(20)    NOT NULL DEFAULT 'Present',
        [Comment]            NVARCHAR(500)   NULL,
        [ScannedAt]          DATETIME2       NULL,
        [MinutesLate]        INT             NULL,
        [Source]             NVARCHAR(20)    NOT NULL,
        [MarkedBy]           NVARCHAR(256)   NOT NULL,
        [PsAttendanceCode]   AS (CASE [Status]
                                    WHEN 'Tardy'   THEN 'T'
                                    WHEN 'Absent'  THEN 'UA'
                                    WHEN 'Excused' THEN 'UA'
                                    ELSE ''
                                END) PERSISTED,
        [CreatedDate]        DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [ModifiedDate]       DATETIME2       NULL,
        CONSTRAINT [PK_TC_ClassAttendance]
            PRIMARY KEY CLUSTERED ([AttendanceId] ASC),
        CONSTRAINT [UQ_TC_ClassAttendance_SectionStudentDate]
            UNIQUE NONCLUSTERED ([ClassSectionId], [StudentDcid], [AttendanceDate]),
        CONSTRAINT [FK_TC_ClassAttendance_ClassSection]
            FOREIGN KEY ([ClassSectionId]) REFERENCES [dbo].[TC_ClassSection]([ClassSectionId]),
        CONSTRAINT [FK_TC_ClassAttendance_District]
            FOREIGN KEY ([DistrictId]) REFERENCES [dbo].[TC_District]([DistrictId]),
        CONSTRAINT [FK_TC_ClassAttendance_Campus]
            FOREIGN KEY ([CampusId]) REFERENCES [dbo].[Attendance_Campuses]([CampusId]),
        CONSTRAINT [CK_TC_ClassAttendance_Status]
            CHECK ([Status] IN ('Present', 'Tardy', 'Absent', 'Excused', 'EarlyOut')),
        CONSTRAINT [CK_TC_ClassAttendance_Source]
            CHECK ([Source] IN ('TeacherManual', 'QrPhone', 'ClassroomKiosk', 'Inferred', 'BulkPresent'))
    );

    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendance_Section_Date]
        ON [dbo].[TC_ClassAttendance] ([ClassSectionId], [AttendanceDate])
        INCLUDE ([StudentDcid], [StudentNumber], [Status], [Comment], [ScannedAt], [MinutesLate], [PsAttendanceCode]);

    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendance_Student_Date]
        ON [dbo].[TC_ClassAttendance] ([StudentDcid], [AttendanceDate])
        INCLUDE ([ClassSectionId], [Status], [PsAttendanceCode]);

    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendance_Campus_Date]
        ON [dbo].[TC_ClassAttendance] ([CampusId], [AttendanceDate])
        INCLUDE ([ClassSectionId], [StudentDcid], [Status], [PsAttendanceCode]);

    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendance_District_Date]
        ON [dbo].[TC_ClassAttendance] ([DistrictId], [AttendanceDate])
        INCLUDE ([CampusId], [Status]);

    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendance_NonPresent_Date]
        ON [dbo].[TC_ClassAttendance] ([AttendanceDate], [CampusId])
        INCLUDE ([ClassSectionId], [StudentDcid], [StudentNumber], [Status], [Comment])
        WHERE [Status] <> 'Present';

    -- Filter uses the source Status column instead of PsAttendanceCode
    -- because SQL Server (Msg 10609) does not allow filtered indexes
    -- to reference computed columns in their filter expression, even
    -- when the computed column is PERSISTED. The set of rows is the
    -- same: Status IN ('Tardy','Absent','Excused') matches exactly
    -- the rows where PsAttendanceCode is non-empty.
    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendance_PsCode_Date]
        ON [dbo].[TC_ClassAttendance] ([AttendanceDate], [PsAttendanceCode])
        INCLUDE ([ClassSectionId], [StudentDcid])
        WHERE [Status] IN ('Tardy', 'Absent', 'Excused');

    PRINT 'Created TC_ClassAttendance table';
END
ELSE
BEGIN
    PRINT 'TC_ClassAttendance table already exists - skipping create';
END
GO

-- =====================================================================
-- Verification (table is empty until Phase C teacher grid starts writing)
-- =====================================================================
SELECT
    name AS ConstraintName,
    type_desc AS ConstraintType
FROM sys.objects
WHERE parent_object_id = OBJECT_ID('[dbo].[TC_ClassAttendance]')
  AND type IN ('PK', 'UQ', 'F', 'C')
ORDER BY type_desc, name;

SELECT
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    i.has_filter AS HasFilter,
    i.filter_definition AS FilterDef
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('[dbo].[TC_ClassAttendance]')
  AND i.type > 0
ORDER BY i.name;

SELECT
    c.name AS ColumnName,
    cc.definition AS ComputedExpression,
    cc.is_persisted AS IsPersisted
FROM sys.computed_columns cc
JOIN sys.columns c ON c.object_id = cc.object_id AND c.column_id = cc.column_id
WHERE cc.object_id = OBJECT_ID('[dbo].[TC_ClassAttendance]');
