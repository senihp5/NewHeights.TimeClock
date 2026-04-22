-- Migration 044: Reception specialized sub pool — two columns
-- Date: 2026-04-21
-- Purpose: Implement Option B from the Patrick 2026-04-21 design call:
--          strict isolation between teacher subs (classroom coverage) and
--          reception subs (front-desk / admin coverage). Teacher subs never
--          appear as candidates for a reception absence; reception subs
--          never appear for a classroom absence.
--
-- New columns:
--   TC_Employees.SubRole       NVARCHAR(20) NULL   TEACHER | RECEPTION
--   TC_SubRequests.RequestType NVARCHAR(20) NULL   TEACHER | RECEPTION
--
-- NULL handling: any existing row with NULL SubRole or NULL RequestType is
-- treated as TEACHER by application code, which preserves current behavior
-- for the 13-sub teacher pool that was the only shape the system knew
-- before this migration.
--
-- No seed data — reception subs are onboarded manually (HR updates SubRole
-- to RECEPTION for each relevant TcEmployee via the admin UI when they're
-- hired into that specialty).
--
-- GO separators required: the filtered indexes reference columns added in
-- the earlier blocks, which need to exist in the parsed schema at index
-- parse time. (See migration 042 post-mortem for the same trap.)

-- ── Step 1: Add TC_Employees.SubRole ─────────────────────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE Name = N'SubRole'
      AND Object_ID = Object_ID(N'TC_Employees')
)
BEGIN
    ALTER TABLE TC_Employees
        ADD SubRole NVARCHAR(20) NULL;

    PRINT 'Added TC_Employees.SubRole';
END
ELSE PRINT 'Skip: TC_Employees.SubRole already exists.';
GO

-- ── Step 2: Add TC_SubRequests.RequestType ───────────────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE Name = N'RequestType'
      AND Object_ID = Object_ID(N'TC_SubRequests')
)
BEGIN
    ALTER TABLE TC_SubRequests
        ADD RequestType NVARCHAR(20) NULL;

    PRINT 'Added TC_SubRequests.RequestType';
END
ELSE PRINT 'Skip: TC_SubRequests.RequestType already exists.';
GO

-- ── Step 3: Filtered indexes ─────────────────────────────────────────
-- Most subs are TEACHER (legacy default), most requests are TEACHER — index
-- only the RECEPTION rows since those are the rare case and the filter by
-- role/type will be hitting them most often in the new flows.

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TC_Employees_SubRole_Reception'
      AND object_id = OBJECT_ID('TC_Employees')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_TC_Employees_SubRole_Reception
        ON TC_Employees (SubRole)
        WHERE SubRole = 'RECEPTION';

    PRINT 'Added IX_TC_Employees_SubRole_Reception';
END
ELSE PRINT 'Skip: IX_TC_Employees_SubRole_Reception already exists.';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TC_SubRequests_RequestType_Reception'
      AND object_id = OBJECT_ID('TC_SubRequests')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_TC_SubRequests_RequestType_Reception
        ON TC_SubRequests (RequestType)
        WHERE RequestType = 'RECEPTION';

    PRINT 'Added IX_TC_SubRequests_RequestType_Reception';
END
ELSE PRINT 'Skip: IX_TC_SubRequests_RequestType_Reception already exists.';
GO

PRINT 'Migration 044 complete.';
