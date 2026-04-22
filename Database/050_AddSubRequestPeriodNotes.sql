-- Migration 050: per-period class notes + attachments on sub requests
-- Date: 2026-04-22
-- Purpose: Teachers need to leave lesson notes and links (OneDrive / Google
--          Drive / SharePoint / etc.) for the substitute covering their
--          absence. One row per period lets notes be tailored per period
--          (different lesson plan per class). Attachments table is typed
--          so it can hold URL links today and Azure Blob references later
--          without a schema change.
--
-- Schema:
--   TC_SubRequestPeriodNotes
--     - One row per (SubRequestId, PeriodIdentifier)
--     - Notes: nvarchar(max) free text
--   TC_SubRequestPeriodAttachments
--     - Many rows per note, typed (URL today, BLOB future)
--     - URL type stores paste-links from cloud drives
--     - BLOB type (future) stores container + key for Azure Blob Storage
--       uploads, fetched via SAS URL at download time
--
-- Idempotent. GO separators per the 042 lesson.

-- ── Step 1: TC_SubRequestPeriodNotes ─────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'TC_SubRequestPeriodNotes')
BEGIN
    CREATE TABLE TC_SubRequestPeriodNotes (
        PeriodNoteId      BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SubRequestId      BIGINT         NOT NULL,
        PeriodIdentifier  NVARCHAR(20)   NOT NULL,
        Notes             NVARCHAR(MAX)  NULL,
        CreatedBy         NVARCHAR(100)  NULL,
        CreatedDate       DATETIME       NOT NULL DEFAULT GETDATE(),
        ModifiedBy        NVARCHAR(100)  NULL,
        ModifiedDate      DATETIME       NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_TC_SubRequestPeriodNotes_SubRequest
            FOREIGN KEY (SubRequestId) REFERENCES TC_SubRequests(SubRequestId)
            ON DELETE CASCADE
    );

    PRINT 'Created TC_SubRequestPeriodNotes';
END
ELSE PRINT 'Skip: TC_SubRequestPeriodNotes already exists.';
GO

-- Uniqueness: one note row per (request, period). Prevents dupes if the
-- save path ever races.

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UX_TC_SubRequestPeriodNotes_RequestPeriod'
      AND object_id = OBJECT_ID('TC_SubRequestPeriodNotes')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_TC_SubRequestPeriodNotes_RequestPeriod
        ON TC_SubRequestPeriodNotes (SubRequestId, PeriodIdentifier);

    PRINT 'Added UX_TC_SubRequestPeriodNotes_RequestPeriod';
END
ELSE PRINT 'Skip: UX_TC_SubRequestPeriodNotes_RequestPeriod already exists.';
GO

-- ── Step 2: TC_SubRequestPeriodAttachments ───────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'TC_SubRequestPeriodAttachments')
BEGIN
    CREATE TABLE TC_SubRequestPeriodAttachments (
        AttachmentId     BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        PeriodNoteId     BIGINT         NOT NULL,
        AttachmentType   NVARCHAR(10)   NOT NULL DEFAULT 'URL',  -- URL | BLOB
        Label            NVARCHAR(200)  NULL,
        Url              NVARCHAR(1000) NULL,   -- cloud link for URL type, SAS download URL cached for BLOB type
        BlobContainer    NVARCHAR(100)  NULL,   -- future: Azure Blob container name
        BlobKey          NVARCHAR(500)  NULL,   -- future: Azure Blob key/path inside container
        ContentType      NVARCHAR(100)  NULL,   -- future: MIME type for downloaded blobs
        SizeBytes        BIGINT         NULL,   -- future: file size for display
        UploadedBy       NVARCHAR(100)  NULL,
        UploadedAt       DATETIME       NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_TC_SubRequestPeriodAttachments_PeriodNote
            FOREIGN KEY (PeriodNoteId) REFERENCES TC_SubRequestPeriodNotes(PeriodNoteId)
            ON DELETE CASCADE,
        CONSTRAINT CK_TC_SubRequestPeriodAttachments_Type
            CHECK (AttachmentType IN ('URL', 'BLOB'))
    );

    PRINT 'Created TC_SubRequestPeriodAttachments';
END
ELSE PRINT 'Skip: TC_SubRequestPeriodAttachments already exists.';
GO

-- Hot-path index: retrieve all attachments for a period note row.

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TC_SubRequestPeriodAttachments_PeriodNoteId'
      AND object_id = OBJECT_ID('TC_SubRequestPeriodAttachments')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_TC_SubRequestPeriodAttachments_PeriodNoteId
        ON TC_SubRequestPeriodAttachments (PeriodNoteId)
        INCLUDE (AttachmentType, Label, Url);

    PRINT 'Added IX_TC_SubRequestPeriodAttachments_PeriodNoteId';
END
ELSE PRINT 'Skip: IX_TC_SubRequestPeriodAttachments_PeriodNoteId already exists.';
GO

PRINT 'Migration 050 complete.';
