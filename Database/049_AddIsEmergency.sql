-- Migration 049: IsEmergency flag on TC_SubRequests
-- Date: 2026-04-21 (session 3d — Phase A extension)
-- Purpose: Supervisors + admins + campus admins can mark a new substitute
--          request as Emergency Fill for same-day must-fill positions.
--          Emergency requests get broadcast to every candidate simultaneously
--          (instead of sequential cascade), carry a 30-minute token window
--          (down from the default 2h), and display an "URGENT" banner in
--          emails and on the supervisor outreach panel.
--
-- The flag lives on TC_SubRequests so it's stable across the request's
-- lifecycle — outreach dispatch logic reads it at each send, and the
-- partial-acceptance flow from migration 048 still works as-is (first to
-- accept wins; partial coverage still supported).
--
-- Idempotent: IF NOT EXISTS guard.

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE Name = N'IsEmergency'
      AND Object_ID = Object_ID(N'TC_SubRequests')
)
BEGIN
    ALTER TABLE TC_SubRequests
        ADD IsEmergency BIT NOT NULL DEFAULT 0;

    PRINT 'Added TC_SubRequests.IsEmergency';
END
ELSE PRINT 'Skip: TC_SubRequests.IsEmergency already exists.';
GO

-- Filtered index for the common "show me all emergency requests" query.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TC_SubRequests_IsEmergency'
      AND object_id = OBJECT_ID('TC_SubRequests')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_TC_SubRequests_IsEmergency
        ON TC_SubRequests (IsEmergency)
        WHERE IsEmergency = 1;

    PRINT 'Added IX_TC_SubRequests_IsEmergency';
END
ELSE PRINT 'Skip: IX_TC_SubRequests_IsEmergency already exists.';
GO

PRINT 'Migration 049 complete.';
