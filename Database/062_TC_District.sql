-- =====================================================================
-- Migration 062: Create TC_District + add Attendance_Campuses.DistrictId
-- =====================================================================
-- Purpose:
--   Scaffold multi-district support so future expansion to additional
--   school districts does not require a schema migration on every
--   roster / attendance / class-attendance table. New Heights Adult
--   High School is seeded as DistrictId = 1.
--
--   Foundation for the Class Attendance feature (migrations 063-067).
--   Every TC_ClassSection / TC_ClassAttendance / TC_ClassAttendanceSheet
--   row will carry a denormalized DistrictId so district-wide reports
--   don't need multi-hop joins.
--
-- Anchors:
--   - Attendance_Campuses is the existing table for Campus rows.
--   - TC_District.CmsConnectionStringName + PowerSchoolBaseUrl let a
--     future district point at its own CMS DB and PS instance via App
--     Service config without code changes.
--
-- Rollback:
--   ALTER TABLE [dbo].[Attendance_Campuses] DROP CONSTRAINT [FK_Attendance_Campuses_District];
--   ALTER TABLE [dbo].[Attendance_Campuses] DROP COLUMN [DistrictId];
--   DROP TABLE [dbo].[TC_District];
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = 'TC_District')
BEGIN
    CREATE TABLE [dbo].[TC_District]
    (
        [DistrictId]                INT             IDENTITY(1, 1) NOT NULL,
        [DistrictCode]              NVARCHAR(20)    NOT NULL,
        [DistrictName]              NVARCHAR(100)   NOT NULL,
        [TimeZone]                  NVARCHAR(50)    NOT NULL DEFAULT 'Central Standard Time',
        [CmsConnectionStringName]   NVARCHAR(50)    NOT NULL DEFAULT 'DefaultConnection',
        [PowerSchoolBaseUrl]        NVARCHAR(200)   NULL,
        [IsActive]                  BIT             NOT NULL DEFAULT 1,
        [Notes]                     NVARCHAR(500)   NULL,
        [CreatedDate]               DATETIME2       NOT NULL DEFAULT SYSDATETIME(),
        [ModifiedDate]              DATETIME2       NULL,
        CONSTRAINT [PK_TC_District]
            PRIMARY KEY CLUSTERED ([DistrictId] ASC),
        CONSTRAINT [UQ_TC_District_DistrictCode]
            UNIQUE NONCLUSTERED ([DistrictCode])
    );

    CREATE NONCLUSTERED INDEX [IX_TC_District_IsActive]
        ON [dbo].[TC_District] ([IsActive]);

    PRINT 'Created TC_District table';
END
ELSE
BEGIN
    PRINT 'TC_District table already exists - skipping create';
END
GO

-- =====================================================================
-- Seed the New Heights Adult High School district as DistrictId = 1.
-- Explicit IDENTITY_INSERT so existing rows can FK to DistrictId = 1
-- without depending on the auto-increment landing on 1.
-- =====================================================================
IF NOT EXISTS (SELECT 1 FROM [dbo].[TC_District] WHERE [DistrictCode] = 'NHAHS')
BEGIN
    SET IDENTITY_INSERT [dbo].[TC_District] ON;

    INSERT INTO [dbo].[TC_District]
        ([DistrictId], [DistrictCode], [DistrictName], [TimeZone],
         [CmsConnectionStringName], [PowerSchoolBaseUrl], [IsActive], [Notes])
    VALUES
        (1, 'NHAHS', 'New Heights Adult High School', 'Central Standard Time',
         'DefaultConnection', 'https://newheightsed.powerschool.com', 1,
         'Initial seed - sole district at launch');

    SET IDENTITY_INSERT [dbo].[TC_District] OFF;

    PRINT 'Seeded NHAHS district as DistrictId 1';
END
ELSE
BEGIN
    PRINT 'NHAHS district already seeded - skipping';
END
GO

-- =====================================================================
-- Add DistrictId to Attendance_Campuses, backfill, then enforce
-- NOT NULL + FK. Three-step ALTER avoids a NULL / NOT-NULL race on
-- existing rows when DEFAULT can't be applied at ADD time.
-- =====================================================================
IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE name = 'DistrictId'
      AND object_id = OBJECT_ID('[dbo].[Attendance_Campuses]')
)
BEGIN
    ALTER TABLE [dbo].[Attendance_Campuses]
        ADD [DistrictId] INT NULL;

    PRINT 'Added DistrictId column to Attendance_Campuses (nullable)';
END
ELSE
BEGIN
    PRINT 'Attendance_Campuses.DistrictId already exists - skipping ADD';
END
GO

UPDATE [dbo].[Attendance_Campuses]
   SET [DistrictId] = 1
 WHERE [DistrictId] IS NULL;

PRINT 'Backfilled DistrictId = 1 on existing Attendance_Campuses rows';
GO

IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE name = 'DistrictId'
      AND object_id = OBJECT_ID('[dbo].[Attendance_Campuses]')
      AND is_nullable = 1
)
BEGIN
    ALTER TABLE [dbo].[Attendance_Campuses]
        ALTER COLUMN [DistrictId] INT NOT NULL;

    PRINT 'Set Attendance_Campuses.DistrictId NOT NULL';
END
ELSE
BEGIN
    PRINT 'Attendance_Campuses.DistrictId already NOT NULL - skipping';
END
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_Attendance_Campuses_District'
)
BEGIN
    ALTER TABLE [dbo].[Attendance_Campuses]
        ADD CONSTRAINT [FK_Attendance_Campuses_District]
        FOREIGN KEY ([DistrictId]) REFERENCES [dbo].[TC_District]([DistrictId]);

    PRINT 'Added FK_Attendance_Campuses_District';
END
ELSE
BEGIN
    PRINT 'FK_Attendance_Campuses_District already exists - skipping';
END
GO

-- =====================================================================
-- Verification
-- =====================================================================
SELECT DistrictId, DistrictCode, DistrictName, TimeZone, IsActive
FROM [dbo].[TC_District];

SELECT CampusId, CampusCode, CampusName, DistrictId
FROM [dbo].[Attendance_Campuses]
ORDER BY CampusId;
