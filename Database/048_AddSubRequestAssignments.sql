-- Migration 048: Phase A — partial period acceptance on sub requests
-- Date: 2026-04-21 (session 3c)
-- Purpose: Allow a TcSubRequest to be covered by multiple subs, each owning a
--          subset of the requested periods. Before this migration, a request
--          could only be assigned to a single sub (AssignedSubEmployeeId). The
--          new join table is authoritative for "who covers which periods."
--
-- Schema:
--   TC_SubRequestAssignments: one row per partial-accept. The sum of all
--   PeriodsCovered across a request's assignments equals the originally
--   requested PeriodsNeeded when the request is fully covered.
--
--   TC_SubRequests.PartialStallAlertSentAt: dedup column for the hourly
--   background service that flags supervisors when a request stays
--   PartiallyAssigned past the 24h threshold.
--
-- Legacy AssignedSubEmployeeId on TC_SubRequests stays in place for backward
-- compatibility with pre-Phase-A rows. New partial accepts do NOT write it;
-- queries that need "the primary sub" should look at the first assignment
-- row by AcceptedAt ASC.
--
-- Idempotent. GO separators per migration 042 lesson.

-- ── Step 1: TC_SubRequestAssignments table ───────────────────────────

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = N'TC_SubRequestAssignments')
BEGIN
    CREATE TABLE TC_SubRequestAssignments (
        AssignmentId    BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SubRequestId    BIGINT         NOT NULL,
        SubEmployeeId   INT            NOT NULL,
        PeriodsCovered  NVARCHAR(200)  NOT NULL,
        AcceptedAt      DATETIME       NOT NULL DEFAULT GETDATE(),
        AssignedBy      NVARCHAR(100)  NULL,
        CreatedDate     DATETIME       NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_TC_SubRequestAssignments_SubRequest
            FOREIGN KEY (SubRequestId) REFERENCES TC_SubRequests(SubRequestId)
            ON DELETE CASCADE,
        CONSTRAINT FK_TC_SubRequestAssignments_SubEmployee
            FOREIGN KEY (SubEmployeeId) REFERENCES TC_Employees(EmployeeId)
    );

    PRINT 'Created TC_SubRequestAssignments';
END
ELSE PRINT 'Skip: TC_SubRequestAssignments already exists.';
GO

-- ── Step 2: Index for per-request lookup (hot path for accept + display) ─

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TC_SubRequestAssignments_SubRequestId'
      AND object_id = OBJECT_ID('TC_SubRequestAssignments')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_TC_SubRequestAssignments_SubRequestId
        ON TC_SubRequestAssignments (SubRequestId)
        INCLUDE (SubEmployeeId, PeriodsCovered, AcceptedAt);

    PRINT 'Added IX_TC_SubRequestAssignments_SubRequestId';
END
ELSE PRINT 'Skip: IX_TC_SubRequestAssignments_SubRequestId already exists.';
GO

-- ── Step 3: PartialStallAlertSentAt on TC_SubRequests ────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE Name = N'PartialStallAlertSentAt'
      AND Object_ID = Object_ID(N'TC_SubRequests')
)
BEGIN
    ALTER TABLE TC_SubRequests
        ADD PartialStallAlertSentAt DATETIME NULL;

    PRINT 'Added TC_SubRequests.PartialStallAlertSentAt';
END
ELSE PRINT 'Skip: TC_SubRequests.PartialStallAlertSentAt already exists.';
GO

PRINT 'Migration 048 complete.';
