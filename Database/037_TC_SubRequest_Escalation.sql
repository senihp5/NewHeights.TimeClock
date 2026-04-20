-- =============================================================================
-- Migration 037: Phase 9c — Sub request escalation tracking
-- Date: 2026-04-20 (Phase 9c)
-- Idempotent — safe to re-run.
--
-- Adds EscalatedAt DATETIME2 NULL to TC_SubRequests so the background service
-- can track the last time an AwaitingSub request was escalated to the campus
-- admin, avoiding duplicate notifications within the re-escalation interval.
-- =============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TC_SubRequests')
      AND name = 'EscalatedAt'
)
BEGIN
    ALTER TABLE dbo.TC_SubRequests
        ADD EscalatedAt DATETIME2 NULL;
END
GO

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_TCSubRequests_Status_EscalatedAt'
      AND object_id = OBJECT_ID('dbo.TC_SubRequests')
)
BEGIN
    CREATE INDEX IX_TCSubRequests_Status_EscalatedAt
        ON dbo.TC_SubRequests (Status, EscalatedAt, CreatedDate)
        INCLUDE (RequestingEmployeeId, CampusId);
END
GO
