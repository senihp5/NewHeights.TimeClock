/*
===============================================================================
New Heights TimeClock System - Database Migration Script
Version: 2.0
Date: 2026-03-12
Database: IDSuite3 (Azure SQL)

School context:
  - Adult-only school (no minors, no parents/guardians)
  - College-style schedule: Day session and Night session per campus
  - Some staff teach both Day and Night
  - Salaried staff have expected hour windows (not bell-period-driven)
  - Hourly staff clock in/out normally; session context derived from punch time

Adds:
  1. TC_TimePunches        - PunchSubType, SectionId, PunchSource columns
  2. TC_AuditLog           - Expanded schema (v1.0 stub renamed to TC_AuditLog_v1)
  3. TC_PunchCorrections   - Receptionist/admin correction workflow
  4. TC_BellSchedule       - Per-campus, per-session named schedules (uploadable)
  5. TC_BellPeriods        - Individual class periods under each schedule
  6. TC_StaffHoursWindow   - Expected in/out windows for salaried staff by session
  7. TC_AttendanceRules    - Per-campus tardy/absent threshold config
  8. TC_SystemConfig       - New config keys

IMPORTANT:
  - Idempotent - safe to run multiple times.
  - TC_CorrectionRequests (v1.0 employee self-service table) is PRESERVED.
  - TC_AuditLog v1.0 is renamed to TC_AuditLog_v1 and replaced.
  - Run 001 migration before this script.
===============================================================================
*/

SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'TimeClock Database Migration v2.0';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';
GO

-------------------------------------------------------------------------------
-- SECTION 1: Add columns to TC_TimePunches
--
-- PunchSubType: context of the punch (STANDARD, LUNCH, MEETING, MEDICAL,
--               EARLY_ARRIVAL, CLASS_CHECKIN, CLASS_CHECKOUT,
--               CAMPUS_ARRIVAL, CAMPUS_DEPARTURE)
-- SectionId:    PowerSchool section ID for classroom QR punches
-- PunchSource:  KIOSK, MOBILE, MANUAL, SYSTEM, QR_CAMPUS, QR_CLASS
-- SessionType:  DAY or NIGHT - derived from punch time, stored for reporting
--
-- All nullable, no enforcement at this stage.
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 1: Adding columns to TC_TimePunches...';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TC_TimePunches') AND name = 'PunchSubType')
BEGIN
    ALTER TABLE TC_TimePunches ADD PunchSubType NVARCHAR(20) NULL;
    PRINT '    Added: PunchSubType';
END
ELSE PRINT '    Skipped: PunchSubType already exists';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TC_TimePunches') AND name = 'SectionId')
BEGIN
    ALTER TABLE TC_TimePunches ADD SectionId NVARCHAR(50) NULL;
    PRINT '    Added: SectionId';
END
ELSE PRINT '    Skipped: SectionId already exists';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TC_TimePunches') AND name = 'PunchSource')
BEGIN
    ALTER TABLE TC_TimePunches ADD PunchSource NVARCHAR(15) NULL;
    PRINT '    Added: PunchSource';
END
ELSE PRINT '    Skipped: PunchSource already exists';

IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TC_TimePunches') AND name = 'SessionType')
BEGIN
    -- DAY, NIGHT, or NULL when not determinable
    ALTER TABLE TC_TimePunches ADD SessionType NVARCHAR(5) NULL;
    PRINT '    Added: SessionType';
END
ELSE PRINT '    Skipped: SessionType already exists';
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TC_TimePunches') AND name = 'IX_TCPunch_SubType')
BEGIN
    CREATE INDEX IX_TCPunch_SubType ON TC_TimePunches(PunchSubType) WHERE PunchSubType IS NOT NULL;
    PRINT '    Created: IX_TCPunch_SubType (filtered)';
END
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TC_TimePunches') AND name = 'IX_TCPunch_Session')
BEGIN
    CREATE INDEX IX_TCPunch_Session ON TC_TimePunches(SessionType) WHERE SessionType IS NOT NULL;
    PRINT '    Created: IX_TCPunch_Session (filtered)';
END
GO

-------------------------------------------------------------------------------
-- SECTION 2: Rebuild TC_AuditLog with expanded v2.0 schema
--
-- v1.0 had a minimal generic schema. v2.0 is purpose-built for TimeClock
-- operations with structured action codes and source tracking.
--
-- Strategy: rename existing table if it has the old schema, create new one.
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 2: Rebuilding TC_AuditLog (v2.0)...';

IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_AuditLog') AND type = 'U')
   AND NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('TC_AuditLog') AND name = 'ActionCode')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_AuditLog_v1') AND type = 'U')
    BEGIN
        EXEC sp_rename 'TC_AuditLog', 'TC_AuditLog_v1';
        PRINT '    Renamed old TC_AuditLog -> TC_AuditLog_v1 (preserved)';
    END
    ELSE
    BEGIN
        DROP TABLE TC_AuditLog;
        PRINT '    Dropped old TC_AuditLog (TC_AuditLog_v1 already existed)';
    END
END

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_AuditLog') AND type = 'U')
BEGIN
    CREATE TABLE TC_AuditLog (
        AuditId         BIGINT          PRIMARY KEY IDENTITY(1,1),

        -- What happened
        -- Examples: PUNCH_CREATED, PUNCH_MODIFIED, PUNCH_DELETED,
        -- CORRECTION_SUBMITTED, CORRECTION_APPROVED, CORRECTION_REJECTED,
        -- MANUAL_ENTRY_CREATED, TIMECARD_APPROVED, PAYROLL_EXPORTED,
        -- EMPLOYEE_ACTIVATED, EMPLOYEE_DEACTIVATED, CONFIG_CHANGED, LOGIN
        ActionCode      NVARCHAR(30)    NOT NULL,

        -- Who did it
        UserId          NVARCHAR(100)   NOT NULL,
        UserName        NVARCHAR(150)   NOT NULL,
        UserEmail       NVARCHAR(200)   NULL,
        UserRole        NVARCHAR(30)    NULL,

        -- What was affected
        EntityType      NVARCHAR(30)    NOT NULL,
        -- PUNCH, CORRECTION, TIMECARD, EMPLOYEE, PAY_PERIOD, BELL_SCHEDULE, CONFIG, SYSTEM
        EntityId        NVARCHAR(50)    NOT NULL,

        -- Denormalized FK shortcuts for fast lookups
        PunchId         BIGINT          NULL,
        CorrectionId    BIGINT          NULL,
        EmployeeId      INT             NULL,
        CampusId        INT             NULL,

        -- The change
        OldValuesJson   NVARCHAR(MAX)   NULL,
        NewValuesJson   NVARCHAR(MAX)   NULL,
        DeltaSummary    NVARCHAR(500)   NULL,

        -- Why
        Reason          NVARCHAR(500)   NULL,

        -- Where from
        -- KIOSK, MOBILE, ADMIN_UI, RECEPTION_UI, SYSTEM, API
        Source          NVARCHAR(15)    NOT NULL DEFAULT 'SYSTEM',
        IPAddress       NVARCHAR(50)    NULL,
        SessionId       NVARCHAR(100)   NULL,

        CreatedDate     DATETIME2       NOT NULL DEFAULT GETUTCDATE()
    );

    CREATE INDEX IX_TCAudit_ActionCode ON TC_AuditLog(ActionCode);
    CREATE INDEX IX_TCAudit_UserId     ON TC_AuditLog(UserId);
    CREATE INDEX IX_TCAudit_EntityRef  ON TC_AuditLog(EntityType, EntityId);
    CREATE INDEX IX_TCAudit_PunchId    ON TC_AuditLog(PunchId)      WHERE PunchId IS NOT NULL;
    CREATE INDEX IX_TCAudit_CorrId     ON TC_AuditLog(CorrectionId) WHERE CorrectionId IS NOT NULL;
    CREATE INDEX IX_TCAudit_EmployeeId ON TC_AuditLog(EmployeeId)   WHERE EmployeeId IS NOT NULL;
    CREATE INDEX IX_TCAudit_Date       ON TC_AuditLog(CreatedDate);
    CREATE INDEX IX_TCAudit_CampusDate ON TC_AuditLog(CampusId, CreatedDate) WHERE CampusId IS NOT NULL;

    PRINT '    Created: TC_AuditLog (v2.0 schema)';
END
ELSE PRINT '    Skipped: TC_AuditLog already has v2.0 schema';
GO

-------------------------------------------------------------------------------
-- SECTION 3: TC_PunchCorrections
--
-- Receptionist/Admin correction workflow table.
-- Separate from TC_CorrectionRequests (employee self-service - preserved).
--
-- Safety split:
--   SafetyImmediateApply = 1  -> write to attendance dashboard now
--   AffectsTimeclock = 1      -> queue for supervisor approval before pay impact
--
-- Covers both staff (TC_Employees) and any person in the building.
-- PersonType is STAFF only - adult school, no minors.
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 3: Creating TC_PunchCorrections...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_PunchCorrections') AND type = 'U')
BEGIN
    CREATE TABLE TC_PunchCorrections (
        CorrectionId            BIGINT          PRIMARY KEY IDENTITY(1,1),

        EmployeeId              INT             NULL,
        PersonIdNumber          NVARCHAR(50)    NOT NULL,
        PersonFirstName         NVARCHAR(100)   NULL,
        PersonLastName          NVARCHAR(100)   NULL,
        CampusId                INT             NOT NULL,

        -- Original punch (NULL = adding a new punch where none existed)
        OriginalPunchId         BIGINT          NULL,
        OriginalAttTxId         BIGINT          NULL,
        OriginalDateTime        DATETIME2       NULL,
        OriginalPunchType       NVARCHAR(20)    NULL,
        OriginalSubType         NVARCHAR(20)    NULL,

        -- The proposed correction
        -- ADD_PUNCH, MODIFY_PUNCH, DELETE_PUNCH
        CorrectionType          NVARCHAR(15)    NOT NULL,
        ProposedDateTime        DATETIME2       NULL,
        ProposedPunchType       NVARCHAR(20)    NULL,
        ProposedSubType         NVARCHAR(20)    NULL,
        ProposedScanType        NVARCHAR(20)    NULL,

        -- Required explanation
        Reason                  NVARCHAR(500)   NOT NULL,

        -- The split flag
        SafetyImmediateApply    BIT             NOT NULL DEFAULT 1,
        AffectsTimeclock        BIT             NOT NULL DEFAULT 0,

        -- Workflow status: PENDING, APPROVED, REJECTED, AUTO_APPLIED
        Status                  NVARCHAR(15)    NOT NULL DEFAULT 'PENDING',

        -- Safety write-back
        SafetyAppliedDate       DATETIME2       NULL,
        SafetyAppliedTxId       BIGINT          NULL,

        -- Timeclock approval
        TimeclockApprovedBy     NVARCHAR(100)   NULL,
        TimeclockApprovedDate   DATETIME2       NULL,
        TimeclockAppliedPunchId BIGINT          NULL,
        TimeclockReviewNotes    NVARCHAR(500)   NULL,

        -- Rejection
        RejectedBy              NVARCHAR(100)   NULL,
        RejectedDate            DATETIME2       NULL,
        RejectionReason         NVARCHAR(500)   NULL,

        -- Who submitted
        -- RECEPTION, SUPERVISOR, HR, ADMIN
        SubmittedByUserId       NVARCHAR(100)   NOT NULL,
        SubmittedByName         NVARCHAR(150)   NOT NULL,
        SubmittedByRole         NVARCHAR(30)    NULL,
        SubmittedDate           DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CreatedDate             DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
        ModifiedDate            DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT FK_TCPCorr_Employee  FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId),
        CONSTRAINT FK_TCPCorr_Campus    FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT FK_TCPCorr_OrigPunch FOREIGN KEY (OriginalPunchId) REFERENCES TC_TimePunches(PunchId)
    );

    CREATE INDEX IX_TCPCorr_Status     ON TC_PunchCorrections(Status);
    CREATE INDEX IX_TCPCorr_EmployeeId ON TC_PunchCorrections(EmployeeId) WHERE EmployeeId IS NOT NULL;
    CREATE INDEX IX_TCPCorr_PersonId   ON TC_PunchCorrections(PersonIdNumber);
    CREATE INDEX IX_TCPCorr_Campus     ON TC_PunchCorrections(CampusId);
    CREATE INDEX IX_TCPCorr_Submitted  ON TC_PunchCorrections(SubmittedDate);
    CREATE INDEX IX_TCPCorr_Pending    ON TC_PunchCorrections(Status, AffectsTimeclock) WHERE Status = 'PENDING';

    PRINT '    Created: TC_PunchCorrections';
END
ELSE PRINT '    Skipped: TC_PunchCorrections (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 4: TC_BellSchedule and TC_BellPeriods
--
-- Adult college-style schedule. Two sessions per campus: DAY and NIGHT.
-- Some instructors teach in both sessions - session is on the schedule,
-- not on the employee.
--
-- Designed to be uploaded via Excel (same pattern as pay periods).
-- Upload format (one flat row per period):
--   Col A: CampusCode     (STOPSIX, MCCART)
--   Col B: ScheduleName   (Standard Day, Standard Night, Late Start Day, ...)
--   Col C: SessionType    (DAY, NIGHT)
--   Col D: EffectiveStart (date or blank for open-ended)
--   Col E: EffectiveEnd   (date or blank for no end)
--   Col F: IsDefault      (TRUE/FALSE)
--   Col G: PeriodNumber   (1, 2, 3...)
--   Col H: PeriodName     (Period 1, Lunch, Advisory, ...)
--   Col I: PeriodType     (CLASS, LUNCH, ADVISORY, BREAK)
--   Col J: StartTime      (8:00 AM)
--   Col K: EndTime        (9:30 AM)
--   Col L: TardyThresholdMinutes  (5)
--   Col M: AbsentThresholdMinutes (20)
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 4: Creating TC_BellSchedule and TC_BellPeriods...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_BellSchedule') AND type = 'U')
BEGIN
    CREATE TABLE TC_BellSchedule (
        BellScheduleId      INT             PRIMARY KEY IDENTITY(1,1),
        CampusId            INT             NOT NULL,
        SchoolYear          NVARCHAR(9)     NOT NULL,
        -- e.g. 2025-2026 - matches pay period school year format
        ScheduleName        NVARCHAR(50)    NOT NULL,
        -- e.g. 'Standard Day', 'Standard Night', 'Late Start Day'

        -- DAY, NIGHT
        -- A teacher who teaches both simply has sections in both schedules.
        SessionType         NVARCHAR(5)     NOT NULL DEFAULT 'DAY',

        -- STANDARD, LATE_START, EARLY_RELEASE, TESTING, CUSTOM
        ScheduleType        NVARCHAR(15)    NOT NULL DEFAULT 'STANDARD',

        EffectiveStartDate  DATE            NULL,
        EffectiveEndDate    DATE            NULL,
        IsDefault           BIT             NOT NULL DEFAULT 0,
        IsActive            BIT             NOT NULL DEFAULT 1,
        Notes               NVARCHAR(200)   NULL,
        UploadedBy          NVARCHAR(100)   NULL,
        UploadedDate        DATETIME2       NULL,
        CreatedDate         DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
        ModifiedDate        DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT FK_TCBell_Campus FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId)
    );

    CREATE INDEX IX_TCBell_Campus      ON TC_BellSchedule(CampusId);
    CREATE INDEX IX_TCBell_Year        ON TC_BellSchedule(SchoolYear);
    CREATE INDEX IX_TCBell_Session     ON TC_BellSchedule(CampusId, SessionType);
    CREATE INDEX IX_TCBell_Dates       ON TC_BellSchedule(EffectiveStartDate, EffectiveEndDate);
    CREATE INDEX IX_TCBell_Default     ON TC_BellSchedule(CampusId, SessionType, IsDefault) WHERE IsDefault = 1;

    PRINT '    Created: TC_BellSchedule';
END
ELSE PRINT '    Skipped: TC_BellSchedule (already exists)';
GO

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_BellPeriods') AND type = 'U')
BEGIN
    CREATE TABLE TC_BellPeriods (
        PeriodId                INT             PRIMARY KEY IDENTITY(1,1),
        BellScheduleId          INT             NOT NULL,
        PeriodNumber            INT             NOT NULL,
        PeriodName              NVARCHAR(50)    NOT NULL,
        -- CLASS, LUNCH, ADVISORY, BREAK
        PeriodType              NVARCHAR(15)    NOT NULL DEFAULT 'CLASS',
        StartTime               TIME            NOT NULL,
        EndTime                 TIME            NOT NULL,
        TardyThresholdMinutes   INT             NOT NULL DEFAULT 5,
        AbsentThresholdMinutes  INT             NOT NULL DEFAULT 20,
        IsActive                BIT             NOT NULL DEFAULT 1,

        CONSTRAINT FK_TCBellP_Schedule FOREIGN KEY (BellScheduleId) REFERENCES TC_BellSchedule(BellScheduleId),
        CONSTRAINT UQ_TCBellP_SchedPeriod UNIQUE (BellScheduleId, PeriodNumber)
    );

    CREATE INDEX IX_TCBellP_Schedule ON TC_BellPeriods(BellScheduleId);
    CREATE INDEX IX_TCBellP_Time     ON TC_BellPeriods(StartTime, EndTime);

    PRINT '    Created: TC_BellPeriods';
END
ELSE PRINT '    Skipped: TC_BellPeriods (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 5: TC_StaffHoursWindow
--
-- Defines the expected working hour window for salaried staff per campus
-- and session type. NOT tied to bell periods.
--
-- Salaried staff are expected to be on campus within their window.
-- If a salaried staff member teaches both Day and Night, two rows exist
-- for their campus (one DAY, one NIGHT). The system assigns a punch to
-- a session by comparing punch time to the window for that campus.
--
-- Example:
--   Stop Six | DAY   | 7:30 AM - 3:30 PM  | GracePeriod 15 min
--   Stop Six | NIGHT | 4:30 PM - 10:00 PM | GracePeriod 15 min
--
-- This is a simple config table - HR/Admin manages it, not uploaded via Excel.
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 5: Creating TC_StaffHoursWindow...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_StaffHoursWindow') AND type = 'U')
BEGIN
    CREATE TABLE TC_StaffHoursWindow (
        WindowId                INT             PRIMARY KEY IDENTITY(1,1),
        CampusId                INT             NOT NULL,
        SessionType             NVARCHAR(5)     NOT NULL,
        -- DAY or NIGHT
        SchoolYear              NVARCHAR(9)     NOT NULL,
        -- Matches bell schedule and pay period school year

        -- Expected working window for salaried staff in this session
        ExpectedArrivalTime     TIME            NOT NULL,
        ExpectedDepartureTime   TIME            NOT NULL,

        -- How late before flagging late arrival
        LateArrivalThresholdMin INT             NOT NULL DEFAULT 5,
        -- How early before flagging early departure
        EarlyDepartureThresholdMin INT          NOT NULL DEFAULT 10,
        -- Alert if no punch X minutes after expected arrival
        MissedPunchAlertMin     INT             NOT NULL DEFAULT 15,

        IsActive                BIT             NOT NULL DEFAULT 1,
        Notes                   NVARCHAR(200)   NULL,
        CreatedBy               NVARCHAR(100)   NULL,
        CreatedDate             DATETIME2       NOT NULL DEFAULT GETUTCDATE(),
        ModifiedDate            DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT FK_TCHrsWin_Campus FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT UQ_TCHrsWin_CampusSessionYear UNIQUE (CampusId, SessionType, SchoolYear)
    );

    CREATE INDEX IX_TCHrsWin_Campus  ON TC_StaffHoursWindow(CampusId);
    CREATE INDEX IX_TCHrsWin_Session ON TC_StaffHoursWindow(SessionType);
    CREATE INDEX IX_TCHrsWin_Year    ON TC_StaffHoursWindow(SchoolYear);

    PRINT '    Created: TC_StaffHoursWindow';
END
ELSE PRINT '    Skipped: TC_StaffHoursWindow (already exists)';
GO

-- Seed placeholder rows so HR knows the structure - update times once confirmed
-- These will be updated when actual schedules are provided
IF NOT EXISTS (SELECT 1 FROM TC_StaffHoursWindow)
   AND EXISTS (SELECT 1 FROM Attendance_Campuses WHERE CampusCode IN ('STOP6','MCCART'))
BEGIN
    DECLARE @StopSixId INT, @McCartId INT;
    SELECT @StopSixId = CampusId FROM Attendance_Campuses WHERE CampusCode = 'STOP6';
    SELECT @McCartId  = CampusId FROM Attendance_Campuses WHERE CampusCode = 'MCCART';

    IF @StopSixId IS NOT NULL
    BEGIN
        INSERT INTO TC_StaffHoursWindow
            (CampusId, SessionType, SchoolYear, ExpectedArrivalTime, ExpectedDepartureTime, Notes)
        VALUES
            (@StopSixId, 'DAY',   '2025-2026', '07:30', '15:30', 'Placeholder - confirm with admin'),
            (@StopSixId, 'NIGHT', '2025-2026', '16:30', '22:00', 'Placeholder - confirm with admin');
        PRINT '    Inserted: Stop Six placeholder hour windows';
    END

    IF @McCartId IS NOT NULL
    BEGIN
        INSERT INTO TC_StaffHoursWindow
            (CampusId, SessionType, SchoolYear, ExpectedArrivalTime, ExpectedDepartureTime, Notes)
        VALUES
            (@McCartId, 'DAY',   '2025-2026', '07:30', '15:30', 'Placeholder - confirm with admin'),
            (@McCartId, 'NIGHT', '2025-2026', '16:30', '22:00', 'Placeholder - confirm with admin');
        PRINT '    Inserted: McCart placeholder hour windows';
    END
END
ELSE PRINT '    Skipped: TC_StaffHoursWindow already has data or campuses not found';
GO

-------------------------------------------------------------------------------
-- SECTION 6: TC_AttendanceRules
--
-- Per-campus overridable thresholds. CampusId = NULL = system-wide default.
-- Campus row overrides system default for that campus.
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 6: Creating TC_AttendanceRules...';

IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_AttendanceRules') AND type = 'U')
BEGIN
    CREATE TABLE TC_AttendanceRules (
        RuleId          INT             PRIMARY KEY IDENTITY(1,1),
        CampusId        INT             NULL,
        -- NULL = system-wide default
        -- STAFF, STUDENT, BOTH
        PersonType      NVARCHAR(10)    NOT NULL DEFAULT 'BOTH',
        RuleKey         NVARCHAR(50)    NOT NULL,
        RuleValue       NVARCHAR(100)   NOT NULL,
        -- INT, BOOL, STRING, TIME
        RuleType        NVARCHAR(10)    NOT NULL DEFAULT 'INT',
        Description     NVARCHAR(200)   NULL,
        ModifiedBy      NVARCHAR(100)   NULL,
        ModifiedDate    DATETIME2       NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT FK_TCRules_Campus FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId),
        CONSTRAINT UQ_TCRules_CampusKey UNIQUE (CampusId, PersonType, RuleKey)
    );

    CREATE INDEX IX_TCRules_Campus ON TC_AttendanceRules(CampusId);

    -- System-wide defaults (CampusId = NULL)
    -- Adult students - no parents/guardians involved
    INSERT INTO TC_AttendanceRules (CampusId, PersonType, RuleKey, RuleValue, RuleType, Description) VALUES
        (NULL, 'STUDENT', 'TardyThresholdMinutes',      '5',     'INT',  'Minutes after period start before marked Tardy'),
        (NULL, 'STUDENT', 'AbsentThresholdMinutes',     '20',    'INT',  'Minutes after period start before marked Absent'),
        (NULL, 'STUDENT', 'EarlyDepartureAlertMin',     '30',    'INT',  'Minutes before period end to flag early departure'),
        (NULL, 'STAFF',   'LateArrivalThresholdMin',    '5',     'INT',  'Minutes after expected arrival before flagged late'),
        (NULL, 'STAFF',   'EarlyDepartureThresholdMin', '10',    'INT',  'Minutes before expected departure before flagged early'),
        (NULL, 'STAFF',   'MissedPunchAlertMin',        '15',    'INT',  'Minutes past expected arrival before missed-punch alert'),
        (NULL, 'BOTH',    'AutoCloseOpenPunches',       'true',  'BOOL', 'Auto-close unclosed punches at end of day'),
        (NULL, 'BOTH',    'AutoCloseAtTime',            '23:59', 'TIME', 'Time to auto-close open punches if enabled'),
        (NULL, 'STUDENT', 'RequireGeofenceOnMobile',    'true',  'BOOL', 'Require student to be on campus for mobile check-in'),
        (NULL, 'STAFF',   'RequireGeofenceOnMobile',    'false', 'BOOL', 'Require staff geofence for mobile clock-in');

    PRINT '    Created: TC_AttendanceRules with system defaults';
END
ELSE PRINT '    Skipped: TC_AttendanceRules (already exists)';
GO

-------------------------------------------------------------------------------
-- SECTION 7: New TC_SystemConfig keys
-------------------------------------------------------------------------------
PRINT '';
PRINT '>>> Section 7: Adding TC_SystemConfig keys...';

IF EXISTS (SELECT 1 FROM sys.objects WHERE object_id = OBJECT_ID('TC_SystemConfig') AND type = 'U')
BEGIN
    IF NOT EXISTS (SELECT 1 FROM TC_SystemConfig WHERE ConfigKey = 'Correction.SafetyAutoApply')
        INSERT INTO TC_SystemConfig (ConfigKey, ConfigValue, ConfigType, Description) VALUES
        ('Correction.SafetyAutoApply', 'true', 'BOOL',
         'Reception corrections apply to attendance dashboard immediately');

    IF NOT EXISTS (SELECT 1 FROM TC_SystemConfig WHERE ConfigKey = 'Correction.TimeclockRequiresApproval')
        INSERT INTO TC_SystemConfig (ConfigKey, ConfigValue, ConfigType, Description) VALUES
        ('Correction.TimeclockRequiresApproval', 'true', 'BOOL',
         'Corrections affecting hourly pay require supervisor approval');

    IF NOT EXISTS (SELECT 1 FROM TC_SystemConfig WHERE ConfigKey = 'Correction.MaxBackdateDays')
        INSERT INTO TC_SystemConfig (ConfigKey, ConfigValue, ConfigType, Description) VALUES
        ('Correction.MaxBackdateDays', '30', 'INT',
         'Maximum days in the past a correction can be submitted for');

    IF NOT EXISTS (SELECT 1 FROM TC_SystemConfig WHERE ConfigKey = 'Correction.RequireReasonMinLength')
        INSERT INTO TC_SystemConfig (ConfigKey, ConfigValue, ConfigType, Description) VALUES
        ('Correction.RequireReasonMinLength', '10', 'INT',
         'Minimum character length for correction reason field');

    IF NOT EXISTS (SELECT 1 FROM TC_SystemConfig WHERE ConfigKey = 'BellSchedule.DefaultSessionGapMinutes')
        INSERT INTO TC_SystemConfig (ConfigKey, ConfigValue, ConfigType, Description) VALUES
        ('BellSchedule.DefaultSessionGapMinutes', '60', 'INT',
         'Minimum gap between DAY session end and NIGHT session start for session detection');

    IF NOT EXISTS (SELECT 1 FROM TC_SystemConfig WHERE ConfigKey = 'Student.MobileCheckinEnabled')
        INSERT INTO TC_SystemConfig (ConfigKey, ConfigValue, ConfigType, Description) VALUES
        ('Student.MobileCheckinEnabled', 'true', 'BOOL',
         'Allow students to check in via mobile QR camera scan');

    IF NOT EXISTS (SELECT 1 FROM TC_SystemConfig WHERE ConfigKey = 'Student.ClassQRCheckinEnabled')
        INSERT INTO TC_SystemConfig (ConfigKey, ConfigValue, ConfigType, Description) VALUES
        ('Student.ClassQRCheckinEnabled', 'true', 'BOOL',
         'Allow students to check in to class via classroom QR code');

    IF NOT EXISTS (SELECT 1 FROM TC_SystemConfig WHERE ConfigKey = 'Audit.RetentionDays')
        INSERT INTO TC_SystemConfig (ConfigKey, ConfigValue, ConfigType, Description) VALUES
        ('Audit.RetentionDays', '2555', 'INT',
         'Days to retain audit log records (default 7 years)');

    PRINT '    TC_SystemConfig keys inserted';
END
ELSE PRINT '    WARNING: TC_SystemConfig not found - run 001 migration first';
GO

-------------------------------------------------------------------------------
-- SECTION 8: Summary
-------------------------------------------------------------------------------
PRINT '';
PRINT '========================================';
PRINT 'Migration v2.0 Complete!';
PRINT 'Finished: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';
PRINT '';
PRINT 'Changes applied:';
PRINT '  TC_TimePunches     : Added PunchSubType, SectionId, PunchSource, SessionType';
PRINT '  TC_AuditLog        : Rebuilt with v2.0 schema (v1.0 preserved as TC_AuditLog_v1)';
PRINT '  TC_PunchCorrections: NEW - receptionist/admin correction workflow';
PRINT '  TC_BellSchedule    : NEW - per-campus, per-session named schedule (uploadable)';
PRINT '  TC_BellPeriods     : NEW - individual class periods per schedule';
PRINT '  TC_StaffHoursWindow: NEW - expected hour windows for salaried staff by session';
PRINT '  TC_AttendanceRules : NEW - per-campus tardy/absent thresholds';
PRINT '  TC_SystemConfig    : New keys for corrections, bell schedule, student check-in';
PRINT '';
PRINT 'NEXT STEPS:';
PRINT '  1. Run this script against IDSuite3 in Azure SQL';
PRINT '  2. Update TC_StaffHoursWindow placeholder times once schedules are confirmed';
PRINT '  3. Upload bell schedules via the Admin > Bell Schedule page (once built)';
PRINT '  4. Override TC_AttendanceRules per campus if thresholds differ by campus';
PRINT '';
PRINT 'DO NOT RUN before migration 001 has been applied.';
GO
