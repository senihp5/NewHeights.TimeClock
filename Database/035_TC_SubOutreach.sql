-- =============================================================================
-- Migration 035: Substitute scheduling — TC_SubPool + TC_SubRequests + TC_SubOutreach
-- Date: 2026-04-16 (Phase 5)
-- Idempotent — safe to re-run.
--
-- This migration is self-sufficient: it will create the base TC_SubPool and
-- TC_SubRequests tables if migration 001 did not create them in this database,
-- then add the Phase 5 scheduling columns + TC_SubOutreach on top.
-- =============================================================================

-- 035a: TC_SubPool (substitute roster; baseline schema matches migration 001 section 9)
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_SubPool')
BEGIN
    CREATE TABLE TC_SubPool (
        SubPoolId            INT IDENTITY(1,1) PRIMARY KEY,
        EmployeeId           INT NULL,
        ExternalName         NVARCHAR(100) NULL,
        ExternalEmail        NVARCHAR(100) NULL,
        ExternalPhone        NVARCHAR(20) NULL,
        CertificationType    NVARCHAR(50) NULL,
        QualifiedSubjects    NVARCHAR(500) NULL,
        QualifiedGradeLevels NVARCHAR(100) NULL,
        AvailableCampuses    NVARCHAR(50) NULL,
        IsActive             BIT NOT NULL DEFAULT 1,
        Notes                NVARCHAR(500) NULL,
        CreatedDate          DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
        CONSTRAINT FK_TCSubPool_Employee FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId)
    );

    CREATE INDEX IX_TCSubPool_Employee ON TC_SubPool(EmployeeId);
    CREATE INDEX IX_TCSubPool_Active ON TC_SubPool(IsActive);

    PRINT 'Migration 035a: Created TC_SubPool.';
END
ELSE
    PRINT 'Migration 035a: TC_SubPool already exists — skipped.';

-- 035b: TC_SubRequests (baseline schema matches migration 001 section 10)
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_SubRequests')
BEGIN
    CREATE TABLE TC_SubRequests (
        SubRequestId           BIGINT IDENTITY(1,1) PRIMARY KEY,
        RequestingEmployeeId   INT NOT NULL,
        CampusId               INT NOT NULL,
        StartDate              DATE NOT NULL,
        EndDate                DATE NOT NULL,
        AbsenceType            NVARCHAR(20) NOT NULL,
        AbsenceReason          NVARCHAR(500) NULL,
        PeriodsNeeded          NVARCHAR(50) NULL,
        SubjectArea            NVARCHAR(100) NULL,
        SpecialInstructions    NVARCHAR(1000) NULL,
        AssignedSubPoolId      INT NULL,
        AssignedDate           DATETIME2 NULL,
        AssignedBy             NVARCHAR(100) NULL,
        Status                 NVARCHAR(15) NOT NULL DEFAULT 'Submitted',
        SupervisorApprovedBy   NVARCHAR(100) NULL,
        SupervisorApprovedDate DATETIME2 NULL,
        CalendarEventId        NVARCHAR(200) NULL,
        IsCalendarSynced       BIT NOT NULL DEFAULT 0,
        CreatedDate            DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
        ModifiedDate           DATETIME2 NOT NULL DEFAULT SYSDATETIME(),
        CONSTRAINT FK_TCSubReq_Employee FOREIGN KEY (RequestingEmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT FK_TCSubReq_Campus   FOREIGN KEY (CampusId)             REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT FK_TCSubReq_Sub      FOREIGN KEY (AssignedSubPoolId)    REFERENCES TC_SubPool(SubPoolId)
    );

    CREATE INDEX IX_TCSubReq_Employee ON TC_SubRequests(RequestingEmployeeId);
    CREATE INDEX IX_TCSubReq_Dates ON TC_SubRequests(StartDate, EndDate);
    CREATE INDEX IX_TCSubReq_Status ON TC_SubRequests(Status);
    CREATE INDEX IX_TCSubReq_Campus ON TC_SubRequests(CampusId);

    PRINT 'Migration 035b: Created TC_SubRequests.';
END
ELSE
    PRINT 'Migration 035b: TC_SubRequests already exists — skipped.';

-- 035c: Add Phase 5 scheduling columns to TC_SubRequests
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_SubRequests') AND name = 'SessionType'
)
BEGIN
    ALTER TABLE TC_SubRequests ADD SessionType NVARCHAR(10) NULL;
    PRINT 'Migration 035c: Added SessionType to TC_SubRequests.';
END

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_SubRequests') AND name = 'AssignedSubEmployeeId'
)
BEGIN
    ALTER TABLE TC_SubRequests ADD AssignedSubEmployeeId INT NULL;
    PRINT 'Migration 035c: Added AssignedSubEmployeeId to TC_SubRequests.';
END

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_SubRequests') AND name = 'ConfirmationSentAt'
)
BEGIN
    ALTER TABLE TC_SubRequests ADD ConfirmationSentAt DATETIME2 NULL;
    PRINT 'Migration 035c: Added ConfirmationSentAt to TC_SubRequests.';
END

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys
    WHERE name = 'FK_SubRequest_AssignedSub'
)
BEGIN
    ALTER TABLE TC_SubRequests ADD CONSTRAINT FK_SubRequest_AssignedSub
        FOREIGN KEY (AssignedSubEmployeeId) REFERENCES TC_Employees(EmployeeId);
    PRINT 'Migration 035c: Added FK_SubRequest_AssignedSub.';
END

-- 035d: Create TC_SubOutreach
IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_SubOutreach')
BEGIN
    CREATE TABLE TC_SubOutreach (
        OutreachId          BIGINT IDENTITY(1,1) PRIMARY KEY,
        SubRequestId        BIGINT NOT NULL,
        SubEmployeeId       INT NOT NULL,
        OutreachMethod      NVARCHAR(10) NOT NULL DEFAULT 'EMAIL',
        PhoneNumber         NVARCHAR(20) NULL,
        EmailAddress        NVARCHAR(200) NULL,
        ResponseToken       NVARCHAR(64) NOT NULL,
        TokenExpiresAt      DATETIME2 NOT NULL,
        MessageSentAt       DATETIME2 NULL,
        MessageId           NVARCHAR(100) NULL,
        DeliveryStatus      NVARCHAR(20) NOT NULL DEFAULT 'PENDING',
        ResponseStatus      NVARCHAR(20) NOT NULL DEFAULT 'AWAITING',
        RespondedAt         DATETIME2 NULL,
        SentBy              NVARCHAR(200) NULL,
        SequenceOrder       INT NOT NULL DEFAULT 1,
        Notes               NVARCHAR(500) NULL,
        CreatedDate         DATETIME2 NOT NULL DEFAULT SYSDATETIME(),

        CONSTRAINT FK_SubOutreach_Request FOREIGN KEY (SubRequestId)  REFERENCES TC_SubRequests(SubRequestId),
        CONSTRAINT FK_SubOutreach_Sub     FOREIGN KEY (SubEmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT UQ_SubOutreach_Token   UNIQUE (ResponseToken)
    );

    CREATE INDEX IX_SubOutreach_Token          ON TC_SubOutreach(ResponseToken);
    CREATE INDEX IX_SubOutreach_Request        ON TC_SubOutreach(SubRequestId);
    CREATE INDEX IX_SubOutreach_ResponseStatus ON TC_SubOutreach(ResponseStatus);
    CREATE INDEX IX_SubOutreach_TokenExpiresAt ON TC_SubOutreach(TokenExpiresAt);

    PRINT 'Migration 035d: Created TC_SubOutreach with 4 indexes.';
END
ELSE
    PRINT 'Migration 035d: TC_SubOutreach already exists — skipped.';

-- 035e: SMS opt-out tracking on TC_Employees
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_Employees') AND name = 'SmsOptedOut'
)
BEGIN
    ALTER TABLE TC_Employees ADD SmsOptedOut BIT NOT NULL
        CONSTRAINT DF_Employees_SmsOptedOut DEFAULT 0;
    PRINT 'Migration 035e: Added SmsOptedOut to TC_Employees.';
END
ELSE
    PRINT 'Migration 035e: TC_Employees.SmsOptedOut already exists — skipped.';

PRINT 'Migration 035 complete.';
