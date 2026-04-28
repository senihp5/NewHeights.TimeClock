/*
===============================================================================
Migration 059 — TC_SubOutreach.ResponseTimeSeconds (computed)
Date: 2026-04-27
Purpose:
  Surface sub response latency as a queryable column for future analytics —
  average time-to-accept by sub, by campus, by season, decline-rate by sub,
  cascade-stage performance, etc.

  The data already exists implicitly: every accept and decline writes
  RespondedAt (SubOutreachService lines 451, 631 + the outreach-token expiry
  paths); the matching outreach row already has MessageSentAt populated when
  the message went out. This migration just exposes the diff as a PERSISTED
  computed column + a filtered index optimized for analytics queries against
  responded rows only.

  ResponseTimeSeconds is read-only; SQL Server maintains the value whenever
  RespondedAt or MessageSentAt change. EF treats it as ValueGeneratedOnAddOrUpdate
  so app-side inserts/updates won''t try to round-trip a value.

  Idempotent.
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 059: Add TC_SubOutreach.ResponseTimeSeconds';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_SubOutreach')
      AND name = 'ResponseTimeSeconds'
)
BEGIN
    ALTER TABLE TC_SubOutreach
        ADD ResponseTimeSeconds AS (
            CASE
                WHEN MessageSentAt IS NOT NULL AND RespondedAt IS NOT NULL
                THEN DATEDIFF(SECOND, MessageSentAt, RespondedAt)
            END
        ) PERSISTED;
    PRINT '    Added computed column: ResponseTimeSeconds';
END
ELSE PRINT '    Skipped: ResponseTimeSeconds already exists';
GO

-- Filtered index for analytics queries (avg/min/max by sub, by campus, etc.).
-- Filter to terminal response states so the index stays small and excludes
-- AWAITING / EXPIRED / NO_RESPONSE rows where the metric is meaningless.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_SubOutreach_ResponseAnalytics'
      AND object_id = OBJECT_ID('TC_SubOutreach')
)
BEGIN
    CREATE INDEX IX_SubOutreach_ResponseAnalytics
        ON TC_SubOutreach (ResponseStatus, SubEmployeeId)
        INCLUDE (ResponseTimeSeconds, MessageSentAt, RespondedAt, SubRequestId)
        WHERE ResponseStatus IN ('ACCEPTED', 'DECLINED');
    PRINT '    Added analytics index: IX_SubOutreach_ResponseAnalytics';
END
ELSE PRINT '    Skipped: IX_SubOutreach_ResponseAnalytics already exists';
GO

PRINT 'Migration 059 complete.';
GO
