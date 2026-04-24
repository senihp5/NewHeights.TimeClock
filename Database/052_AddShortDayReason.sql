/*
===============================================================================
Migration 052 — ShortDayReason + ShortDayNote on TC_DailyTimecards
Date: 2026-04-23
Purpose:
  Capture WHY a day has fewer hours than scheduled (Weather Closure, PTO,
  Sick, Personal, Holiday, Professional Dev, Other). Employee picks on
  MyTimesheet, supervisor sees it on TeamTimesheets, HR uses it for payroll
  audit. Also written by the Hourly CSV import service when an admin tags a
  reason on an imported row.

  Feature B — follow-on to the Weather Closure ask on 2026-04-23.

Columns:
  - ShortDayReason NVARCHAR(20) NULL — enum string, NULL = normal full day
      Values: 'WeatherClosure', 'PTO', 'Sick', 'Personal', 'Holiday',
              'ProfessionalDev', 'Other'
  - ShortDayNote   NVARCHAR(500) NULL — optional free text context

Idempotent.
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 052: ShortDayReason + ShortDayNote';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_DailyTimecards')
      AND name = 'ShortDayReason'
)
BEGIN
    ALTER TABLE TC_DailyTimecards ADD ShortDayReason NVARCHAR(20) NULL;
    PRINT '    Added column: ShortDayReason';
END
ELSE PRINT '    Skipped: ShortDayReason already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_DailyTimecards')
      AND name = 'ShortDayNote'
)
BEGIN
    ALTER TABLE TC_DailyTimecards ADD ShortDayNote NVARCHAR(500) NULL;
    PRINT '    Added column: ShortDayNote';
END
ELSE PRINT '    Skipped: ShortDayNote already exists';
GO

-- Filtered index — most rows will be NULL; only the short-day rows need fast lookup.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TCDaily_ShortDayReason'
      AND object_id = OBJECT_ID('TC_DailyTimecards')
)
BEGIN
    CREATE INDEX IX_TCDaily_ShortDayReason
        ON TC_DailyTimecards(ShortDayReason)
        WHERE ShortDayReason IS NOT NULL;
    PRINT '    Added filtered index on ShortDayReason';
END
ELSE PRINT '    Skipped: index already exists';
GO

PRINT 'Migration 052 complete.';
GO
