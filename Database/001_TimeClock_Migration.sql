/*
===============================================================================
New Heights TimeClock System - Database Migration Script
Version: 1.0
Date: 2026-02-26
Database: IDSuite3 (Azure SQL)

IMPORTANT: Run this script against the existing IDSuite3 database.
This script is idempotent - safe to run multiple times.
===============================================================================
*/

SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'TimeClock Database Migration v1.0';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';
GO

-------------------------------------------------------------------------------
-- SECTION 1: ALTER Attendance_Campuses (Add Geofence Columns)
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 1: Adding geofence columns to Attendance_Campuses...';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Attendance_Campuses') AND name = 'Latitude')
BEGIN
    ALTER TABLE Attendance_Campuses ADD Latitude DECIMAL(9,6) NULL;
    PRINT '    Added: Latitude';
END
ELSE PRINT '    Skipped: Latitude (already exists)';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Attendance_Campuses') AND name = 'Longitude')
BEGIN
    ALTER TABLE Attendance_Campuses ADD Longitude DECIMAL(9,6) NULL;
    PRINT '    Added: Longitude';
END
ELSE PRINT '    Skipped: Longitude (already exists)';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Attendance_Campuses') AND name = 'GeofenceRadiusMeters')
BEGIN
    ALTER TABLE Attendance_Campuses ADD GeofenceRadiusMeters INT NOT NULL DEFAULT 150;
    PRINT '    Added: GeofenceRadiusMeters';
END
ELSE PRINT '    Skipped: GeofenceRadiusMeters (already exists)';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Attendance_Campuses') AND name = 'CampusWifiSSID')
BEGIN
    ALTER TABLE Attendance_Campuses ADD CampusWifiSSID NVARCHAR(100) NULL;
    PRINT '    Added: CampusWifiSSID';
END
ELSE PRINT '    Skipped: CampusWifiSSID (already exists)';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Attendance_Campuses') AND name = 'DefaultStartTime')
BEGIN
    ALTER TABLE Attendance_Campuses ADD DefaultStartTime TIME NULL;
    PRINT '    Added: DefaultStartTime';
END
ELSE PRINT '    Skipped: DefaultStartTime (already exists)';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Attendance_Campuses') AND name = 'DefaultEndTime')
BEGIN
    ALTER TABLE Attendance_Campuses ADD DefaultEndTime TIME NULL;
    PRINT '    Added: DefaultEndTime';
END
ELSE PRINT '    Skipped: DefaultEndTime (already exists)';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Attendance_Campuses') AND name = 'LunchStartTime')
BEGIN
    ALTER TABLE Attendance_Campuses ADD LunchStartTime TIME NULL;
    PRINT '    Added: LunchStartTime';
END
ELSE PRINT '    Skipped: LunchStartTime (already exists)';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Attendance_Campuses') AND name = 'LunchEndTime')
BEGIN
    ALTER TABLE Attendance_Campuses ADD LunchEndTime TIME NULL;
    PRINT '    Added: LunchEndTime';
END
ELSE PRINT '    Skipped: LunchEndTime (already exists)';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('Attendance_Campuses') AND name = 'GracePeriodMinutes')
BEGIN
    ALTER TABLE Attendance_Campuses ADD GracePeriodMinutes INT NOT NULL DEFAULT 5;
    PRINT '    Added: GracePeriodMinutes';
END
ELSE PRINT '    Skipped: GracePeriodMinutes (already exists)';
GO

PRINT '';
PRINT '>>> Updating campus geofence coordinates...';

UPDATE Attendance_Campuses SET
    Latitude = 32.7197,
    Longitude = -97.2836,
    CampusWifiSSID = 'NewHeights-StopSix',
    DefaultStartTime = '07:30',
    DefaultEndTime = '15:30',
    LunchStartTime = '11:00',
    LunchEndTime = '13:00'
WHERE CampusCode = 'STOP6' AND Latitude IS NULL;

UPDATE Attendance_Campuses SET
    Latitude = 32.7023,
    Longitude = -97.3392,
    CampusWifiSSID = 'NewHeights-McCart',
    DefaultStartTime = '07:30',
    DefaultEndTime = '15:30',
    LunchStartTime = '11:00',
    LunchEndTime = '13:00'
WHERE CampusCode = 'MCCART' AND Latitude IS NULL;

PRINT '    Campus coordinates updated (if not already set)';
GO

-------------------------------------------------------------------------------
-- SECTION 2: TC_PayRules (must be created first - referenced by TC_Employees)
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 2: Creating TC_PayRules...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_PayRules') AND type = 'U')
BEGIN
    CREATE TABLE TC_PayRules (
        PayRuleId INT PRIMARY KEY IDENTITY(1,1),
        RuleName NVARCHAR(50) NOT NULL,
        Description NVARCHAR(200) NULL,
        RoundingIntervalMinutes INT NOT NULL DEFAULT 15,
        RoundingMethod NVARCHAR(10) NOT NULL DEFAULT 'NEAREST',
        GracePeriodMinutes INT NOT NULL DEFAULT 5,
        OvertimeThresholdWeeklyHours DECIMAL(5,2) NOT NULL DEFAULT 40.00,
        OvertimeMultiplier DECIMAL(3,2) NOT NULL DEFAULT 1.50,
        PayPeriodType NVARCHAR(20) NOT NULL DEFAULT 'BIWEEKLY',
        AutoDeductLunchMinutes INT NOT NULL DEFAULT 0,
        RequireLunchPunchAfterHours DECIMAL(4,2) NOT NULL DEFAULT 6.00,
        IsActive BIT NOT NULL DEFAULT 1,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ModifiedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT UQ_TCPayRules_Name UNIQUE (RuleName)
    );
    PRINT '    Created: TC_PayRules';
    
    INSERT INTO TC_PayRules (RuleName, Description, RoundingIntervalMinutes, RoundingMethod, OvertimeThresholdWeeklyHours, PayPeriodType) VALUES
        ('Teacher Default', 'Standard teacher time tracking', 15, 'NEAREST', 40.00, 'SEMIMONTHLY'),
        ('Hourly Staff', 'Hourly support staff', 15, 'NEAREST', 40.00, 'BIWEEKLY'),
        ('Substitute', 'Daily substitute teacher', 15, 'NEAREST', 40.00, 'BIWEEKLY');
    PRINT '    Inserted: Default pay rules';
END
ELSE PRINT '    Skipped: TC_PayRules (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 3: TC_Employees
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 3: Creating TC_Employees...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_Employees') AND type = 'U')
BEGIN
    CREATE TABLE TC_Employees (
        EmployeeId INT PRIMARY KEY IDENTITY(1,1),
        StaffDcid INT NOT NULL,
        IdNumber NVARCHAR(50) NOT NULL,
        AscenderEmployeeId NVARCHAR(50) NULL,
        EmployeeType NVARCHAR(20) NOT NULL DEFAULT 'HOURLY_STAFF',
        HomeCampusId INT NOT NULL,
        SupervisorEmployeeId INT NULL,
        DepartmentCode NVARCHAR(20) NULL,
        DepartmentName NVARCHAR(100) NULL,
        PayRuleId INT NULL,
        HireDate DATE NULL,
        Email NVARCHAR(200) NULL,
        Phone NVARCHAR(20) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        EntraObjectId NVARCHAR(100) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ModifiedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCEmp_Staff FOREIGN KEY (StaffDcid) REFERENCES Staff(Dcid),
        CONSTRAINT FK_TCEmp_Campus FOREIGN KEY (HomeCampusId) REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT FK_TCEmp_Supervisor FOREIGN KEY (SupervisorEmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT FK_TCEmp_PayRule FOREIGN KEY (PayRuleId) REFERENCES TC_PayRules(PayRuleId)
    );

    CREATE UNIQUE INDEX IX_TCEmp_StaffDcid ON TC_Employees(StaffDcid);
    CREATE UNIQUE INDEX IX_TCEmp_IdNumber ON TC_Employees(IdNumber);
    CREATE INDEX IX_TCEmp_Campus ON TC_Employees(HomeCampusId);
    CREATE INDEX IX_TCEmp_Supervisor ON TC_Employees(SupervisorEmployeeId);
    CREATE INDEX IX_TCEmp_Type ON TC_Employees(EmployeeType);
    CREATE INDEX IX_TCEmp_Entra ON TC_Employees(EntraObjectId);
    PRINT '    Created: TC_Employees';
END
ELSE PRINT '    Skipped: TC_Employees (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 4: TC_TimePunches
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 4: Creating TC_TimePunches...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_TimePunches') AND type = 'U')
BEGIN
    CREATE TABLE TC_TimePunches (
        PunchId BIGINT PRIMARY KEY IDENTITY(1,1),
        EmployeeId INT NOT NULL,
        CampusId INT NOT NULL,
        TerminalId INT NULL,
        PunchType NVARCHAR(10) NOT NULL,
        PunchDateTime DATETIME2 NOT NULL,
        RoundedDateTime DATETIME2 NULL,
        Latitude DECIMAL(9,6) NULL,
        Longitude DECIMAL(9,6) NULL,
        GeofenceStatus NVARCHAR(15) NOT NULL DEFAULT 'VERIFIED',
        VerificationMethod NVARCHAR(10) NOT NULL DEFAULT 'GPS',
        ScanMethod NVARCHAR(10) NOT NULL DEFAULT 'QR',
        QRCodeScanned NVARCHAR(200) NULL,
        PunchStatus NVARCHAR(15) NOT NULL DEFAULT 'ACTIVE',
        PairedPunchId BIGINT NULL,
        IsManualEntry BIT NOT NULL DEFAULT 0,
        IsModified BIT NOT NULL DEFAULT 0,
        OriginalPunchDateTime DATETIME2 NULL,
        ModifiedBy NVARCHAR(100) NULL,
        ModifiedReason NVARCHAR(200) NULL,
        Notes NVARCHAR(500) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCPunch_Employee FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT FK_TCPunch_Campus FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT FK_TCPunch_Paired FOREIGN KEY (PairedPunchId) REFERENCES TC_TimePunches(PunchId)
    );

    CREATE INDEX IX_TCPunch_Employee ON TC_TimePunches(EmployeeId);
    CREATE INDEX IX_TCPunch_DateTime ON TC_TimePunches(PunchDateTime);
    CREATE INDEX IX_TCPunch_Campus ON TC_TimePunches(CampusId);
    CREATE INDEX IX_TCPunch_Status ON TC_TimePunches(PunchStatus);
    CREATE INDEX IX_TCPunch_Paired ON TC_TimePunches(PairedPunchId);
    CREATE INDEX IX_TCPunch_EmpDate ON TC_TimePunches(EmployeeId, PunchDateTime) INCLUDE (PunchType, RoundedDateTime, PunchStatus);
    PRINT '    Created: TC_TimePunches';
END
ELSE PRINT '    Skipped: TC_TimePunches (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 5: TC_DailyTimecards
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 5: Creating TC_DailyTimecards...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_DailyTimecards') AND type = 'U')
BEGIN
    CREATE TABLE TC_DailyTimecards (
        TimecardId BIGINT PRIMARY KEY IDENTITY(1,1),
        EmployeeId INT NOT NULL,
        CampusId INT NOT NULL,
        WorkDate DATE NOT NULL,
        FirstPunchIn DATETIME2 NULL,
        LastPunchOut DATETIME2 NULL,
        RegularHours DECIMAL(5,2) NOT NULL DEFAULT 0,
        OvertimeHours DECIMAL(5,2) NOT NULL DEFAULT 0,
        TotalHours DECIMAL(5,2) NOT NULL DEFAULT 0,
        LunchMinutes INT NOT NULL DEFAULT 0,
        BreakMinutes INT NOT NULL DEFAULT 0,
        ScheduledStartTime TIME NULL,
        ScheduledEndTime TIME NULL,
        ScheduledHours DECIMAL(5,2) NULL,
        VarianceMinutes INT NOT NULL DEFAULT 0,
        IsLateArrival BIT NOT NULL DEFAULT 0,
        IsEarlyDeparture BIT NOT NULL DEFAULT 0,
        IsMissedPunch BIT NOT NULL DEFAULT 0,
        IsAbsent BIT NOT NULL DEFAULT 0,
        HasException BIT NOT NULL DEFAULT 0,
        ApprovalStatus NVARCHAR(15) NOT NULL DEFAULT 'PENDING',
        ApprovedBy NVARCHAR(100) NULL,
        ApprovedDate DATETIME2 NULL,
        ExceptionNotes NVARCHAR(500) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ModifiedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCCard_Employee FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT FK_TCCard_Campus FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT UQ_TCCard_EmpDate UNIQUE (EmployeeId, WorkDate)
    );

    CREATE INDEX IX_TCCard_Employee ON TC_DailyTimecards(EmployeeId);
    CREATE INDEX IX_TCCard_Date ON TC_DailyTimecards(WorkDate);
    CREATE INDEX IX_TCCard_Status ON TC_DailyTimecards(ApprovalStatus);
    CREATE INDEX IX_TCCard_Exception ON TC_DailyTimecards(HasException) WHERE HasException = 1;
    PRINT '    Created: TC_DailyTimecards';
END
ELSE PRINT '    Skipped: TC_DailyTimecards (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 6: TC_PayPeriods
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 6: Creating TC_PayPeriods...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_PayPeriods') AND type = 'U')
BEGIN
    CREATE TABLE TC_PayPeriods (
        PayPeriodId INT PRIMARY KEY IDENTITY(1,1),
        PeriodName NVARCHAR(50) NOT NULL,
        StartDate DATE NOT NULL,
        EndDate DATE NOT NULL,
        PayDate DATE NULL,
        Status NVARCHAR(15) NOT NULL DEFAULT 'OPEN',
        LockedBy NVARCHAR(100) NULL,
        LockedDate DATETIME2 NULL,
        ExportedDate DATETIME2 NULL,
        ExportedBy NVARCHAR(100) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );

    CREATE INDEX IX_TCPeriod_Dates ON TC_PayPeriods(StartDate, EndDate);
    CREATE INDEX IX_TCPeriod_Status ON TC_PayPeriods(Status);
    PRINT '    Created: TC_PayPeriods';
END
ELSE PRINT '    Skipped: TC_PayPeriods (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 7: TC_PayPeriodSummary
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 7: Creating TC_PayPeriodSummary...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_PayPeriodSummary') AND type = 'U')
BEGIN
    CREATE TABLE TC_PayPeriodSummary (
        SummaryId BIGINT PRIMARY KEY IDENTITY(1,1),
        PayPeriodId INT NOT NULL,
        EmployeeId INT NOT NULL,
        TotalRegularHours DECIMAL(6,2) NOT NULL DEFAULT 0,
        TotalOvertimeHours DECIMAL(6,2) NOT NULL DEFAULT 0,
        TotalHours DECIMAL(6,2) NOT NULL DEFAULT 0,
        DaysWorked INT NOT NULL DEFAULT 0,
        DaysAbsent INT NOT NULL DEFAULT 0,
        DaysLate INT NOT NULL DEFAULT 0,
        ExceptionCount INT NOT NULL DEFAULT 0,
        ApprovalStatus NVARCHAR(15) NOT NULL DEFAULT 'PENDING',
        SupervisorApprovedBy NVARCHAR(100) NULL,
        SupervisorApprovedDate DATETIME2 NULL,
        HRApprovedBy NVARCHAR(100) NULL,
        HRApprovedDate DATETIME2 NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ModifiedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCPPS_Period FOREIGN KEY (PayPeriodId) REFERENCES TC_PayPeriods(PayPeriodId),
        CONSTRAINT FK_TCPPS_Employee FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT UQ_TCPPS_PeriodEmp UNIQUE (PayPeriodId, EmployeeId)
    );

    CREATE INDEX IX_TCPPS_Period ON TC_PayPeriodSummary(PayPeriodId);
    CREATE INDEX IX_TCPPS_Employee ON TC_PayPeriodSummary(EmployeeId);
    CREATE INDEX IX_TCPPS_Status ON TC_PayPeriodSummary(ApprovalStatus);
    PRINT '    Created: TC_PayPeriodSummary';
END
ELSE PRINT '    Skipped: TC_PayPeriodSummary (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 8: TC_CorrectionRequests
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 8: Creating TC_CorrectionRequests...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_CorrectionRequests') AND type = 'U')
BEGIN
    CREATE TABLE TC_CorrectionRequests (
        RequestId BIGINT PRIMARY KEY IDENTITY(1,1),
        EmployeeId INT NOT NULL,
        RequestType NVARCHAR(20) NOT NULL,
        WorkDate DATE NOT NULL,
        RequestedPunchType NVARCHAR(10) NULL,
        RequestedDateTime DATETIME2 NULL,
        OriginalPunchId BIGINT NULL,
        OriginalDateTime DATETIME2 NULL,
        Reason NVARCHAR(500) NOT NULL,
        Status NVARCHAR(15) NOT NULL DEFAULT 'SUBMITTED',
        ReviewedBy NVARCHAR(100) NULL,
        ReviewedDate DATETIME2 NULL,
        ReviewNotes NVARCHAR(500) NULL,
        AppliedPunchId BIGINT NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCCorr_Employee FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT FK_TCCorr_OrigPunch FOREIGN KEY (OriginalPunchId) REFERENCES TC_TimePunches(PunchId),
        CONSTRAINT FK_TCCorr_Applied FOREIGN KEY (AppliedPunchId) REFERENCES TC_TimePunches(PunchId)
    );

    CREATE INDEX IX_TCCorr_Employee ON TC_CorrectionRequests(EmployeeId);
    CREATE INDEX IX_TCCorr_Status ON TC_CorrectionRequests(Status);
    CREATE INDEX IX_TCCorr_Date ON TC_CorrectionRequests(WorkDate);
    PRINT '    Created: TC_CorrectionRequests';
END
ELSE PRINT '    Skipped: TC_CorrectionRequests (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 9: TC_SubPool
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 9: Creating TC_SubPool...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_SubPool') AND type = 'U')
BEGIN
    CREATE TABLE TC_SubPool (
        SubPoolId INT PRIMARY KEY IDENTITY(1,1),
        EmployeeId INT NULL,
        ExternalName NVARCHAR(100) NULL,
        ExternalEmail NVARCHAR(100) NULL,
        ExternalPhone NVARCHAR(20) NULL,
        CertificationType NVARCHAR(50) NULL,
        QualifiedSubjects NVARCHAR(500) NULL,
        QualifiedGradeLevels NVARCHAR(100) NULL,
        AvailableCampuses NVARCHAR(50) NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        Notes NVARCHAR(500) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCSubPool_Employee FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId)
    );

    CREATE INDEX IX_TCSubPool_Employee ON TC_SubPool(EmployeeId);
    CREATE INDEX IX_TCSubPool_Active ON TC_SubPool(IsActive);
    PRINT '    Created: TC_SubPool';
END
ELSE PRINT '    Skipped: TC_SubPool (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 10: TC_SubRequests
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 10: Creating TC_SubRequests...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_SubRequests') AND type = 'U')
BEGIN
    CREATE TABLE TC_SubRequests (
        SubRequestId BIGINT PRIMARY KEY IDENTITY(1,1),
        RequestingEmployeeId INT NOT NULL,
        CampusId INT NOT NULL,
        StartDate DATE NOT NULL,
        EndDate DATE NOT NULL,
        AbsenceType NVARCHAR(20) NOT NULL,
        AbsenceReason NVARCHAR(500) NULL,
        PeriodsNeeded NVARCHAR(50) NULL,
        SubjectArea NVARCHAR(100) NULL,
        SpecialInstructions NVARCHAR(1000) NULL,
        AssignedSubPoolId INT NULL,
        AssignedDate DATETIME2 NULL,
        AssignedBy NVARCHAR(100) NULL,
        Status NVARCHAR(15) NOT NULL DEFAULT 'SUBMITTED',
        SupervisorApprovedBy NVARCHAR(100) NULL,
        SupervisorApprovedDate DATETIME2 NULL,
        CalendarEventId NVARCHAR(200) NULL,
        IsCalendarSynced BIT NOT NULL DEFAULT 0,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ModifiedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCSubReq_Employee FOREIGN KEY (RequestingEmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT FK_TCSubReq_Campus FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT FK_TCSubReq_Sub FOREIGN KEY (AssignedSubPoolId) REFERENCES TC_SubPool(SubPoolId)
    );

    CREATE INDEX IX_TCSubReq_Employee ON TC_SubRequests(RequestingEmployeeId);
    CREATE INDEX IX_TCSubReq_Dates ON TC_SubRequests(StartDate, EndDate);
    CREATE INDEX IX_TCSubReq_Status ON TC_SubRequests(Status);
    CREATE INDEX IX_TCSubReq_Campus ON TC_SubRequests(CampusId);
    PRINT '    Created: TC_SubRequests';
END
ELSE PRINT '    Skipped: TC_SubRequests (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 11: TC_CalendarEvents
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 11: Creating TC_CalendarEvents...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_CalendarEvents') AND type = 'U')
BEGIN
    CREATE TABLE TC_CalendarEvents (
        EventId BIGINT PRIMARY KEY IDENTITY(1,1),
        CampusId INT NOT NULL,
        EmployeeId INT NULL,
        EventType NVARCHAR(20) NOT NULL,
        Title NVARCHAR(200) NOT NULL,
        Description NVARCHAR(1000) NULL,
        StartDate DATE NOT NULL,
        EndDate DATE NOT NULL,
        AllDay BIT NOT NULL DEFAULT 1,
        GraphEventId NVARCHAR(200) NULL,
        SharedCalendarId NVARCHAR(200) NULL,
        PersonalCalendarEventId NVARCHAR(200) NULL,
        LastSyncDate DATETIME2 NULL,
        SyncStatus NVARCHAR(15) NOT NULL DEFAULT 'PENDING',
        SourceType NVARCHAR(20) NOT NULL DEFAULT 'MANUAL',
        SourceId BIGINT NULL,
        CreatedBy NVARCHAR(100) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        ModifiedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCCalEvent_Campus FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT FK_TCCalEvent_Employee FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId)
    );

    CREATE INDEX IX_TCCalEvent_Dates ON TC_CalendarEvents(StartDate, EndDate);
    CREATE INDEX IX_TCCalEvent_Campus ON TC_CalendarEvents(CampusId);
    CREATE INDEX IX_TCCalEvent_Employee ON TC_CalendarEvents(EmployeeId);
    CREATE INDEX IX_TCCalEvent_Sync ON TC_CalendarEvents(SyncStatus);
    PRINT '    Created: TC_CalendarEvents';
END
ELSE PRINT '    Skipped: TC_CalendarEvents (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 12: TC_PayrollExports
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 12: Creating TC_PayrollExports...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_PayrollExports') AND type = 'U')
BEGIN
    CREATE TABLE TC_PayrollExports (
        ExportId BIGINT PRIMARY KEY IDENTITY(1,1),
        PayPeriodId INT NOT NULL,
        ExportFormat NVARCHAR(20) NOT NULL DEFAULT 'CSV',
        ExportMethod NVARCHAR(20) NOT NULL DEFAULT 'FILE',
        FileName NVARCHAR(200) NULL,
        RecordCount INT NULL,
        TotalRegularHours DECIMAL(8,2) NULL,
        TotalOvertimeHours DECIMAL(8,2) NULL,
        Status NVARCHAR(15) NOT NULL DEFAULT 'GENERATED',
        ErrorLog NVARCHAR(MAX) NULL,
        ExportedBy NVARCHAR(100) NOT NULL,
        ExportedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCExport_Period FOREIGN KEY (PayPeriodId) REFERENCES TC_PayPeriods(PayPeriodId)
    );

    CREATE INDEX IX_TCExport_Period ON TC_PayrollExports(PayPeriodId);
    CREATE INDEX IX_TCExport_Date ON TC_PayrollExports(ExportedDate);
    PRINT '    Created: TC_PayrollExports';
END
ELSE PRINT '    Skipped: TC_PayrollExports (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 13: TC_Notifications
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 13: Creating TC_Notifications...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_Notifications') AND type = 'U')
BEGIN
    CREATE TABLE TC_Notifications (
        NotificationId BIGINT PRIMARY KEY IDENTITY(1,1),
        RecipientEmployeeId INT NOT NULL,
        NotificationType NVARCHAR(30) NOT NULL,
        Channel NVARCHAR(10) NOT NULL DEFAULT 'EMAIL',
        Subject NVARCHAR(200) NULL,
        Body NVARCHAR(MAX) NULL,
        ReferenceType NVARCHAR(30) NULL,
        ReferenceId BIGINT NULL,
        Status NVARCHAR(10) NOT NULL DEFAULT 'QUEUED',
        SentDate DATETIME2 NULL,
        ErrorMessage NVARCHAR(500) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        CONSTRAINT FK_TCNotif_Employee FOREIGN KEY (RecipientEmployeeId) REFERENCES TC_Employees(EmployeeId)
    );

    CREATE INDEX IX_TCNotif_Recipient ON TC_Notifications(RecipientEmployeeId);
    CREATE INDEX IX_TCNotif_Status ON TC_Notifications(Status);
    CREATE INDEX IX_TCNotif_Type ON TC_Notifications(NotificationType);
    PRINT '    Created: TC_Notifications';
END
ELSE PRINT '    Skipped: TC_Notifications (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 14: TC_AuditLog
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 14: Creating TC_AuditLog...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_AuditLog') AND type = 'U')
BEGIN
    CREATE TABLE TC_AuditLog (
        AuditId BIGINT PRIMARY KEY IDENTITY(1,1),
        TableName NVARCHAR(50) NOT NULL,
        RecordId NVARCHAR(50) NOT NULL,
        Action NVARCHAR(10) NOT NULL,
        OldValues NVARCHAR(MAX) NULL,
        NewValues NVARCHAR(MAX) NULL,
        ChangedBy NVARCHAR(100) NOT NULL,
        ChangedByRole NVARCHAR(20) NULL,
        IPAddress NVARCHAR(50) NULL,
        Reason NVARCHAR(200) NULL,
        CreatedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );

    CREATE INDEX IX_TCAudit_Table ON TC_AuditLog(TableName, RecordId);
    CREATE INDEX IX_TCAudit_User ON TC_AuditLog(ChangedBy);
    CREATE INDEX IX_TCAudit_Date ON TC_AuditLog(CreatedDate);
    PRINT '    Created: TC_AuditLog';
END
ELSE PRINT '    Skipped: TC_AuditLog (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 15: TC_SystemConfig
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 15: Creating TC_SystemConfig...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_SystemConfig') AND type = 'U')
BEGIN
    CREATE TABLE TC_SystemConfig (
        ConfigKey NVARCHAR(100) PRIMARY KEY,
        ConfigValue NVARCHAR(MAX) NOT NULL,
        ConfigType NVARCHAR(20) NOT NULL DEFAULT 'STRING',
        Description NVARCHAR(200) NULL,
        ModifiedBy NVARCHAR(100) NULL,
        ModifiedDate DATETIME2 NOT NULL DEFAULT GETUTCDATE()
    );

    INSERT INTO TC_SystemConfig (ConfigKey, ConfigValue, ConfigType, Description) VALUES
        ('Geofence.Enabled', 'true', 'BOOL', 'Enable geofence verification for clock-in'),
        ('Geofence.WiFiFallbackEnabled', 'true', 'BOOL', 'Allow WiFi SSID as geofence fallback'),
        ('Geofence.AllowOverride', 'false', 'BOOL', 'Allow clock-in outside geofence with flag'),
        ('Rounding.DefaultIntervalMinutes', '15', 'INT', 'Default punch rounding interval'),
        ('Rounding.DefaultMethod', 'NEAREST', 'STRING', 'NEAREST, UP, DOWN, NONE'),
        ('Overtime.WeeklyThreshold', '40', 'INT', 'Weekly hours before overtime'),
        ('Overtime.Multiplier', '1.5', 'STRING', 'Overtime pay multiplier'),
        ('Lunch.AutoDeductMinutes', '0', 'INT', '0 = punch-based tracking'),
        ('PayPeriod.Type', 'BIWEEKLY', 'STRING', 'WEEKLY, BIWEEKLY, SEMIMONTHLY'),
        ('Notification.MissedClockInMinutes', '15', 'INT', 'Alert after X min past scheduled start'),
        ('Calendar.SharedCalendarName', 'Staff Schedule & Coverage', 'STRING', 'M365 shared calendar'),
        ('Calendar.SyncEnabled', 'true', 'BOOL', 'Enable M365 calendar sync'),
        ('Export.Format', 'CSV', 'STRING', 'Payroll export format'),
        ('Export.AscenderApiEnabled', 'false', 'BOOL', 'Use Ascender API instead of file');

    PRINT '    Created: TC_SystemConfig with default values';
END
ELSE PRINT '    Skipped: TC_SystemConfig (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 16: Summary
-------------------------------------------------------------------------------
PRINT '';
PRINT '========================================';
PRINT 'Migration Complete!';
PRINT 'Finished: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';
PRINT '';
PRINT 'Tables created/verified:';
PRINT '  - Attendance_Campuses (altered for geofence)';
PRINT '  - TC_PayRules';
PRINT '  - TC_Employees';
PRINT '  - TC_TimePunches';
PRINT '  - TC_DailyTimecards';
PRINT '  - TC_PayPeriods';
PRINT '  - TC_PayPeriodSummary';
PRINT '  - TC_CorrectionRequests';
PRINT '  - TC_SubPool';
PRINT '  - TC_SubRequests';
PRINT '  - TC_CalendarEvents';
PRINT '  - TC_PayrollExports';
PRINT '  - TC_Notifications';
PRINT '  - TC_AuditLog';
PRINT '  - TC_SystemConfig';
PRINT '';
PRINT 'NEXT STEPS:';
PRINT '  1. Verify campus GPS coordinates at each front door';
PRINT '  2. Confirm campus WiFi SSID values';
PRINT '  3. Set up initial pay periods';
PRINT '  4. Import employees from Staff table';
GO
