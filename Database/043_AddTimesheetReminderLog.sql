-- Migration 043: TC_TimesheetReminderLog
-- Date: 2026-04-21
-- Purpose: Dedup table for TimesheetReminderService (Phase D3). Each tick of
--          the background service walks pay periods + employees and fires an
--          email reminder when conditions match (e.g. 48h before deadline,
--          employee timesheet still Pending). The log row prevents the same
--          (PayPeriodId, EmployeeId, ReminderType) from firing twice — useful
--          across server bounces and scaler scenarios where the BackgroundService
--          could otherwise re-tick across the same window.
--
-- ReminderType values (string, NVARCHAR(30)):
--   EMPLOYEE_48H        — sent 48h before EmployeeDeadline to hourly/PT/sub
--                         employees with no submitted summary for the period
--   EMPLOYEE_24H        — sent 24h before EmployeeDeadline, same audience
--   SUPERVISOR_DEADLINE — sent on/after EmployeeDeadline to supervisors with
--                         direct reports whose summaries are still Pending
--
-- Unique constraint on (PayPeriodId, EmployeeId, ReminderType) is the dedup
-- guarantee — the service tries to insert and catches the unique-violation
-- as "already sent, skip."

-- ── Step 1: Create the table ─────────────────────────────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = N'TC_TimesheetReminderLog'
)
BEGIN
    CREATE TABLE TC_TimesheetReminderLog (
        ReminderLogId BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        PayPeriodId   INT            NOT NULL,
        EmployeeId    INT            NOT NULL,
        ReminderType  NVARCHAR(30)   NOT NULL,
        SentAt        DATETIME       NOT NULL DEFAULT GETDATE(),
        DeliveryStatus NVARCHAR(15)  NOT NULL DEFAULT 'SENT',
        ErrorMessage  NVARCHAR(500)  NULL,
        CONSTRAINT FK_TC_TimesheetReminderLog_PayPeriod
            FOREIGN KEY (PayPeriodId) REFERENCES TC_PayPeriods(PayPeriodId)
            ON DELETE CASCADE,
        CONSTRAINT FK_TC_TimesheetReminderLog_Employee
            FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId)
            ON DELETE NO ACTION
    );

    PRINT 'Created TC_TimesheetReminderLog';
END
ELSE PRINT 'Skip: TC_TimesheetReminderLog already exists.';
GO

-- ── Step 2: Unique constraint for dedup ──────────────────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'UX_TC_TimesheetReminderLog_PeriodEmployeeType'
      AND object_id = OBJECT_ID('TC_TimesheetReminderLog')
)
BEGIN
    CREATE UNIQUE NONCLUSTERED INDEX UX_TC_TimesheetReminderLog_PeriodEmployeeType
        ON TC_TimesheetReminderLog (PayPeriodId, EmployeeId, ReminderType);

    PRINT 'Added UX_TC_TimesheetReminderLog_PeriodEmployeeType';
END
ELSE PRINT 'Skip: UX_TC_TimesheetReminderLog_PeriodEmployeeType already exists.';
GO

-- ── Step 3: Lookup index for "what was sent recently" queries ────────

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TC_TimesheetReminderLog_SentAt'
      AND object_id = OBJECT_ID('TC_TimesheetReminderLog')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_TC_TimesheetReminderLog_SentAt
        ON TC_TimesheetReminderLog (SentAt DESC);

    PRINT 'Added IX_TC_TimesheetReminderLog_SentAt';
END
ELSE PRINT 'Skip: IX_TC_TimesheetReminderLog_SentAt already exists.';
GO

PRINT 'Migration 043 complete.';
