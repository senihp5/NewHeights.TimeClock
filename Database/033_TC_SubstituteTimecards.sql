-- Migration 033: TC_SubstituteTimecards
-- Date: 2026-04-16
-- Purpose: Period-based timecard for substitutes. One row per substitute per campus per day.
--          Subs are paid per class period, not per hour. This replaces TC_DailyTimecards
--          for EmployeeType.Substitute. Campus-aware approval routing keys off CampusId
--          (not HomeCampusId) so same-day cross-campus work produces two separate rows.
-- References: SubstituteTimesheetSpec.md sections 3.1 and 9.

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_SubstituteTimecards')
BEGIN
    CREATE TABLE TC_SubstituteTimecards (
        SubTimecardId       BIGINT          IDENTITY(1,1) PRIMARY KEY,
        EmployeeId          INT             NOT NULL,
        CampusId            INT             NOT NULL,
        WorkDate            DATE            NOT NULL,
        CheckInPunchId      BIGINT          NULL,
        CheckOutPunchId     BIGINT          NULL,
        TotalPeriodsWorked  INT             NOT NULL DEFAULT 0,
        ApprovalStatus      INT             NOT NULL DEFAULT 0,
        ApprovedBy          NVARCHAR(200)   NULL,
        ApprovedDate        DATETIME2       NULL,
        Notes               NVARCHAR(500)   NULL,
        CreatedDate         DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        ModifiedDate        DATETIME2       NOT NULL DEFAULT SYSDATETIME(),

        CONSTRAINT FK_SubTimecard_Employee
            FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT FK_SubTimecard_Campus
            FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT FK_SubTimecard_CheckIn
            FOREIGN KEY (CheckInPunchId) REFERENCES TC_TimePunches(PunchId),
        CONSTRAINT FK_SubTimecard_CheckOut
            FOREIGN KEY (CheckOutPunchId) REFERENCES TC_TimePunches(PunchId),
        CONSTRAINT UQ_SubTimecard_EmpCampusDate
            UNIQUE (EmployeeId, CampusId, WorkDate)
    );

    CREATE INDEX IX_SubTimecard_WorkDate     ON TC_SubstituteTimecards(WorkDate);
    CREATE INDEX IX_SubTimecard_CampusDate   ON TC_SubstituteTimecards(CampusId, WorkDate);
    CREATE INDEX IX_SubTimecard_EmployeeDate ON TC_SubstituteTimecards(EmployeeId, WorkDate);
    CREATE INDEX IX_SubTimecard_Approval     ON TC_SubstituteTimecards(ApprovalStatus, CampusId, WorkDate);

    PRINT 'Migration 033: Created TC_SubstituteTimecards with FKs and indexes.';
END
ELSE
BEGIN
    PRINT 'Migration 033: TC_SubstituteTimecards already exists. Skipped.';
END
