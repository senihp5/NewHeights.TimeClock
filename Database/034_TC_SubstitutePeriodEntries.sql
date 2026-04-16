-- Migration 034: TC_SubstitutePeriodEntries
-- Date: 2026-04-16
-- Purpose: Per-period record of classes a substitute taught. This is the BILLABLE unit.
--          One row per class period covered. Denormalized teacher/course/room fields
--          preserve the historical view if master schedule changes later.
--          SessionType (DAY / NIGHT) filters the period picker because subs cover both.
-- References: SubstituteTimesheetSpec.md sections 3.2, 3.5, and 9.
-- Depends on: Migration 033 (TC_SubstituteTimecards).

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_SubstitutePeriodEntries')
BEGIN
    CREATE TABLE TC_SubstitutePeriodEntries (
        EntryId             BIGINT          IDENTITY(1,1) PRIMARY KEY,
        SubTimecardId       BIGINT          NOT NULL,
        BellPeriodId        INT             NOT NULL,
        MasterScheduleId    INT             NULL,
        SubRequestId        BIGINT          NULL,
        PeriodNumber        INT             NOT NULL,
        PeriodName          NVARCHAR(50)    NOT NULL,
        StartTime           TIME            NOT NULL,
        EndTime             TIME            NOT NULL,
        TeacherReplaced     NVARCHAR(200)   NULL,
        CourseName          NVARCHAR(200)   NULL,
        ContentArea         NVARCHAR(50)    NULL,
        Room                NVARCHAR(50)    NULL,
        SessionType         NVARCHAR(10)    NOT NULL DEFAULT 'DAY',
        EntrySource         NVARCHAR(20)    NOT NULL DEFAULT 'MANUAL',
        IsVerified          BIT             NOT NULL DEFAULT 0,
        Notes               NVARCHAR(500)   NULL,
        CreatedDate         DATETIME2       NOT NULL DEFAULT SYSDATETIME(),

        CONSTRAINT FK_SubPeriod_Timecard
            FOREIGN KEY (SubTimecardId) REFERENCES TC_SubstituteTimecards(SubTimecardId) ON DELETE CASCADE,
        CONSTRAINT FK_SubPeriod_BellPeriod
            FOREIGN KEY (BellPeriodId) REFERENCES TC_BellPeriods(PeriodId),
        CONSTRAINT FK_SubPeriod_MasterSched
            FOREIGN KEY (MasterScheduleId) REFERENCES TC_MasterSchedule(ScheduleId),
        CONSTRAINT FK_SubPeriod_SubRequest
            FOREIGN KEY (SubRequestId) REFERENCES TC_SubRequests(SubRequestId),
        CONSTRAINT UQ_SubPeriod_CardPeriod
            UNIQUE (SubTimecardId, PeriodNumber)
    );

    CREATE INDEX IX_SubPeriod_Timecard   ON TC_SubstitutePeriodEntries(SubTimecardId);
    CREATE INDEX IX_SubPeriod_BellPeriod ON TC_SubstitutePeriodEntries(BellPeriodId);
    CREATE INDEX IX_SubPeriod_MasterSched ON TC_SubstitutePeriodEntries(MasterScheduleId) WHERE MasterScheduleId IS NOT NULL;
    CREATE INDEX IX_SubPeriod_SubRequest  ON TC_SubstitutePeriodEntries(SubRequestId) WHERE SubRequestId IS NOT NULL;
    CREATE INDEX IX_SubPeriod_Session    ON TC_SubstitutePeriodEntries(SessionType);

    PRINT 'Migration 034: Created TC_SubstitutePeriodEntries with FKs and indexes.';
END
ELSE
BEGIN
    PRINT 'Migration 034: TC_SubstitutePeriodEntries already exists. Skipped.';
END
