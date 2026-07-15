-- =====================================================================
-- Migration 067: Create TC_ClassAttendanceSheet (Class Attendance Phase A final)
-- =====================================================================
-- Purpose:
--   Per-(ClassSectionId, SheetDate) header row that drives the entire
--   class-attendance workflow: teacher Submit -> receptionist Validate
--   (= PS reconciliation done) -> CampusAdmin Reopen if needed.
--
--   This is the table the receptionist console queries to answer
--   "show me every period's sheet for today" - one row per sheet,
--   under 10ms even at district scale.
--
--   Validate IS the PS reconciliation marker. When reception confirms
--   PS matches the sheet, the row's Validated* columns get populated.
--   There is no separate "TransferredToPs" flag - that distinction
--   was deliberately collapsed because reception's whole job during
--   validation is to reconcile PS against the sheet.
--
-- Status state machine (CK_TC_ClassAttendanceSheet_Status):
--   NotStarted -> InProgress -> Submitted -> Validated (terminal)
--                                   |
--                                   v
--                                Rejected -> (teacher fixes) -> InProgress
--   Validated -> Reopened (CampusAdmin+) -> InProgress
--
-- PDF archive:
--   On Validate, IClassSheetArchiveService renders a QuestPDF and
--   uploads to Azure Blob (newheightscmsstorage / container
--   "class-attendance-archives"). ArchivePdfBlobUri holds the
--   full https URI. Re-Validate after a Reopen renders a new
--   PDF; ArchivePdfBlobUri always points at the latest (prior
--   versions are discoverable via blob listing).
--
-- Snapshot count columns:
--   Populated at Submit and again at Validate, so historical
--   reports don't need to re-aggregate TC_ClassAttendance every
--   time. PresentCount + TardyCount + AbsentCount + ExcusedCount +
--   EarlyOutCount = EnrolledCount (active on SheetDate).
--
-- Rollback:
--   DROP TABLE [dbo].[TC_ClassAttendanceSheet];
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_ClassAttendanceSheet')
BEGIN
    CREATE TABLE [dbo].[TC_ClassAttendanceSheet]
    (
        [SheetId]                INT             IDENTITY(1, 1) NOT NULL,
        [ClassSectionId]         INT             NOT NULL,
        [SheetDate]              DATE            NOT NULL,
        [DistrictId]             INT             NOT NULL DEFAULT 1,
        [CampusId]               INT             NOT NULL,
        [TeacherDcid]            INT             NULL,
        [Status]                 NVARCHAR(20)    NOT NULL DEFAULT 'NotStarted',

        [SubmittedAt]            DATETIME2       NULL,
        [SubmittedBy]            NVARCHAR(256)   NULL,
        [ValidatedAt]            DATETIME2       NULL,
        [ValidatedBy]            NVARCHAR(256)   NULL,
        [ValidationNote]         NVARCHAR(1000)  NULL,
        [RejectedAt]             DATETIME2       NULL,
        [RejectedBy]             NVARCHAR(256)   NULL,
        [RejectionReason]        NVARCHAR(500)   NULL,
        [ReopenedAt]             DATETIME2       NULL,
        [ReopenedBy]             NVARCHAR(256)   NULL,
        [ReopenReason]           NVARCHAR(500)   NULL,
        [TeacherResubmitCount]   INT             NOT NULL DEFAULT 0,

        [PresentCount]           INT             NULL,
        [TardyCount]             INT             NULL,
        [AbsentCount]            INT             NULL,
        [ExcusedCount]           INT             NULL,
        [EarlyOutCount]          INT             NULL,
        [EnrolledCount]          INT             NULL,

        [ArchivePdfBlobUri]      NVARCHAR(500)   NULL,
        [ArchivedAt]             DATETIME2       NULL,

        [CreatedDate]            DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [ModifiedDate]           DATETIME2       NULL,

        CONSTRAINT [PK_TC_ClassAttendanceSheet]
            PRIMARY KEY CLUSTERED ([SheetId] ASC),
        CONSTRAINT [UQ_TC_ClassAttendanceSheet_SectionDate]
            UNIQUE NONCLUSTERED ([ClassSectionId], [SheetDate]),
        CONSTRAINT [FK_TC_ClassAttendanceSheet_ClassSection]
            FOREIGN KEY ([ClassSectionId]) REFERENCES [dbo].[TC_ClassSection]([ClassSectionId]),
        CONSTRAINT [FK_TC_ClassAttendanceSheet_District]
            FOREIGN KEY ([DistrictId]) REFERENCES [dbo].[TC_District]([DistrictId]),
        CONSTRAINT [FK_TC_ClassAttendanceSheet_Campus]
            FOREIGN KEY ([CampusId]) REFERENCES [dbo].[Attendance_Campuses]([CampusId]),
        CONSTRAINT [CK_TC_ClassAttendanceSheet_Status]
            CHECK ([Status] IN ('NotStarted', 'InProgress', 'Submitted', 'Rejected', 'Validated', 'Reopened'))
    );

    -- Primary receptionist daily view: "all sheets at my campus today"
    -- Covers status pill rendering + absent/tardy counts + teacher
    -- name display without joining TC_ClassSection on every row.
    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendanceSheet_Campus_Date]
        ON [dbo].[TC_ClassAttendanceSheet] ([CampusId], [SheetDate])
        INCLUDE ([ClassSectionId], [TeacherDcid], [Status], [AbsentCount], [TardyCount], [EnrolledCount], [ValidatedAt]);

    -- District-wide rollups for principal / district dashboards.
    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendanceSheet_District_Date]
        ON [dbo].[TC_ClassAttendanceSheet] ([DistrictId], [SheetDate])
        INCLUDE ([CampusId], [Status], [AbsentCount], [TardyCount]);

    -- Per-teacher completeness report: "did I submit every section
    -- every day this week?" The filtered partial index on TeacherDcid
    -- excludes orphan rows where TeacherDcid is NULL (rare but possible
    -- when a section is loaded before its teacher is resolved).
    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendanceSheet_Teacher_Date]
        ON [dbo].[TC_ClassAttendanceSheet] ([TeacherDcid], [SheetDate])
        INCLUDE ([ClassSectionId], [Status])
        WHERE [TeacherDcid] IS NOT NULL;

    -- Receptionist work queue: "what's awaiting my validation?"
    -- Small tight filtered index on Status='Submitted' rows only,
    -- ordered by SheetDate so the oldest pending sheets surface first.
    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendanceSheet_Submitted]
        ON [dbo].[TC_ClassAttendanceSheet] ([SheetDate], [CampusId])
        INCLUDE ([SheetId], [ClassSectionId], [TeacherDcid], [SubmittedAt])
        WHERE [Status] = 'Submitted';

    -- Archive download endpoint: lookup by BlobUri (rare but cheap to support).
    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendanceSheet_BlobUri]
        ON [dbo].[TC_ClassAttendanceSheet] ([ArchivePdfBlobUri])
        WHERE [ArchivePdfBlobUri] IS NOT NULL;

    PRINT 'Created TC_ClassAttendanceSheet table';
END
ELSE
BEGIN
    PRINT 'TC_ClassAttendanceSheet table already exists - skipping create';
END
GO

-- =====================================================================
-- Verification (table is empty until Phase C teacher Submit writes)
-- =====================================================================
SELECT
    name AS ConstraintName,
    type_desc AS ConstraintType
FROM sys.objects
WHERE parent_object_id = OBJECT_ID('[dbo].[TC_ClassAttendanceSheet]')
  AND type IN ('PK', 'UQ', 'F', 'C')
ORDER BY type_desc, name;

SELECT
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    i.has_filter AS HasFilter,
    i.filter_definition AS FilterDef
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('[dbo].[TC_ClassAttendanceSheet]')
  AND i.type > 0
ORDER BY i.name;
