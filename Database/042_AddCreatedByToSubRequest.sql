-- Migration 042: TC_SubRequests.CreatedByEmployeeId
-- Date: 2026-04-21
-- Purpose: Allow a supervisor or campus admin to submit an absence request on
--          behalf of another employee (typically a teacher who is out and
--          unable to submit themselves). RequestingEmployeeId remains the
--          person whose absence is being requested (the absentee). The new
--          CreatedByEmployeeId nullable column identifies the actor when the
--          two differ — null means the request was self-submitted.
--
-- Display contract:
--   - When CreatedByEmployeeId IS NULL: "Created by {RequestingEmployee}"
--   - When CreatedByEmployeeId IS NOT NULL: "Created by {CreatedByEmployee}
--     for {RequestingEmployee}"
--
-- GO separators are REQUIRED between the three blocks: the filtered index
-- WHERE clause references CreatedByEmployeeId, which must exist in the
-- parsed schema — the ALTER TABLE ADD column has to commit in its own batch
-- before the CREATE INDEX batch is parsed. Running without the GOs triggers
-- Msg 207 "Invalid column name" at the index step.

-- ── Step 1: Add the column ───────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE Name = N'CreatedByEmployeeId'
      AND Object_ID = Object_ID(N'TC_SubRequests')
)
BEGIN
    ALTER TABLE TC_SubRequests
        ADD CreatedByEmployeeId INT NULL;

    PRINT 'Added TC_SubRequests.CreatedByEmployeeId';
END
ELSE PRINT 'Skip: TC_SubRequests.CreatedByEmployeeId already exists.';
GO

-- ── Step 2: Foreign key to TC_Employees ──────────────────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = 'FK_TC_SubRequests_CreatedByEmployee'
)
BEGIN
    ALTER TABLE TC_SubRequests
        ADD CONSTRAINT FK_TC_SubRequests_CreatedByEmployee
        FOREIGN KEY (CreatedByEmployeeId)
        REFERENCES TC_Employees (EmployeeId)
        ON DELETE NO ACTION;

    PRINT 'Added FK_TC_SubRequests_CreatedByEmployee';
END
ELSE PRINT 'Skip: FK_TC_SubRequests_CreatedByEmployee already exists.';
GO

-- ── Step 3: Filtered index for on-behalf-of lookups ──────────────────
-- Most rows are self-submitted (CreatedByEmployeeId NULL), so we only index
-- the rows where the supervisor-on-behalf-of flow fires.

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TC_SubRequests_CreatedByEmployeeId'
      AND object_id = OBJECT_ID('TC_SubRequests')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_TC_SubRequests_CreatedByEmployeeId
        ON TC_SubRequests (CreatedByEmployeeId)
        WHERE CreatedByEmployeeId IS NOT NULL;

    PRINT 'Added IX_TC_SubRequests_CreatedByEmployeeId';
END
ELSE PRINT 'Skip: IX_TC_SubRequests_CreatedByEmployeeId already exists.';
GO

PRINT 'Migration 042 complete.';
