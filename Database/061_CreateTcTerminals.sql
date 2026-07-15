-- =====================================================================
-- Migration 061: Create TC_Terminals table
-- =====================================================================
-- Purpose:
--   Track physical scanning devices (wired kiosks, ESP32 WiFi scanners,
--   tablets, mobile devices) so every scan can be attributed to a known
--   terminal and resolved to a campus + location.
--
--   Replaces the hardcoded TerminalId=0 and LocationId=1 values that
--   ClockInOut.razor and KioskScanService were writing for every punch.
--
-- Anchors:
--   - AttendanceTransaction already has TerminalId + LocationId columns
--     (created in earlier migration). This table is the lookup source
--     for those values.
--   - ESP32 WiFi scanner (/api/v1/punch) resolves CampusId + LocationId
--     from this table via the TerminalId in the request body.
--
-- Rollback:
--   DROP TABLE [dbo].[TC_Terminals];
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_Terminals')
BEGIN
    CREATE TABLE [dbo].[TC_Terminals]
    (
        [TerminalId]          INT             IDENTITY(1001, 1) NOT NULL,
        [TerminalCode]        NVARCHAR(30)    NOT NULL,
        [CampusId]            INT             NOT NULL,
        [LocationId]          INT             NOT NULL DEFAULT 1,
        [LocationDescription] NVARCHAR(100)   NOT NULL DEFAULT '',
        [DeviceType]          NVARCHAR(30)    NOT NULL DEFAULT 'ESP32_KIOSK',
        [TerminalPurpose]     NVARCHAR(30)    NOT NULL DEFAULT 'CAMPUS_CHECKIN',
        [DeviceSecretHash]    NVARCHAR(200)   NULL,
        [IsActive]            BIT             NOT NULL DEFAULT 1,
        [Notes]               NVARCHAR(500)   NULL,
        [LastSeenAt]          DATETIME2       NULL,
        [LastSeenFirmware]    NVARCHAR(30)    NULL,
        [CreatedDate]         DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [ModifiedDate]        DATETIME2       NULL,
        CONSTRAINT [PK_TC_Terminals]
            PRIMARY KEY CLUSTERED ([TerminalId] ASC),
        CONSTRAINT [UQ_TC_Terminals_TerminalCode]
            UNIQUE NONCLUSTERED ([TerminalCode]),
        CONSTRAINT [FK_TC_Terminals_Campuses]
            FOREIGN KEY ([CampusId]) REFERENCES [dbo].[Attendance_Campuses]([CampusId])
    );

    CREATE NONCLUSTERED INDEX [IX_TC_Terminals_CampusId]
        ON [dbo].[TC_Terminals] ([CampusId])
        WHERE [IsActive] = 1;

    CREATE NONCLUSTERED INDEX [IX_TC_Terminals_DeviceType_IsActive]
        ON [dbo].[TC_Terminals] ([DeviceType], [IsActive]);

    CREATE NONCLUSTERED INDEX [IX_TC_Terminals_TerminalPurpose_IsActive]
        ON [dbo].[TC_Terminals] ([TerminalPurpose], [IsActive]);

    PRINT 'Created TC_Terminals table';
END
ELSE
BEGIN
    PRINT 'TC_Terminals table already exists - skipping create';
END
GO

-- =====================================================================
-- Seed initial terminals
-- Update the IDs and locations below to match your actual deployment.
-- Identity seed starts at 1001 so you can predict the first few values.
-- =====================================================================

-- McCart is the first deployment (summer term trial) so it gets TerminalId 1001.
IF NOT EXISTS (SELECT 1 FROM [dbo].[TC_Terminals] WHERE [TerminalCode] = 'MCCART-RECEPTION')
BEGIN
    INSERT INTO [dbo].[TC_Terminals]
        ([TerminalCode], [CampusId], [LocationId], [LocationDescription], [DeviceType], [TerminalPurpose], [IsActive], [Notes])
    VALUES
        ('MCCART-RECEPTION', (SELECT CampusId FROM Attendance_Campuses WHERE CampusCode = 'MCCART'),
         1, 'Reception Desk', 'ESP32_KIOSK', 'CAMPUS_CHECKIN', 1, 'McCart trial kiosk - summer term');
END

IF NOT EXISTS (SELECT 1 FROM [dbo].[TC_Terminals] WHERE [TerminalCode] = 'STOPSIX-RECEPTION')
BEGIN
    INSERT INTO [dbo].[TC_Terminals]
        ([TerminalCode], [CampusId], [LocationId], [LocationDescription], [DeviceType], [TerminalPurpose], [IsActive], [Notes])
    VALUES
        ('STOPSIX-RECEPTION', (SELECT CampusId FROM Attendance_Campuses WHERE CampusCode = 'STOPSIX'),
         1, 'Reception Desk', 'ESP32_KIOSK', 'CAMPUS_CHECKIN', 1, 'Stop Six kiosk - fast-follow after McCart');
END

PRINT 'Seeded initial terminals';
GO

SELECT TerminalId, TerminalCode, CampusId, LocationDescription, DeviceType, TerminalPurpose, IsActive
FROM [dbo].[TC_Terminals]
ORDER BY TerminalId;
