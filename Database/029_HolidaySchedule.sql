-- Migration 029: Holiday Schedule table
-- Date: 2026-04-16
-- Purpose: Store school holiday dates for automatic timesheet integration

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'TC_HolidaySchedule')
BEGIN
    CREATE TABLE TC_HolidaySchedule (
        HolidayId       INT IDENTITY(1,1) PRIMARY KEY,
        HolidayName     NVARCHAR(100) NOT NULL,
        HolidayDate     DATE NOT NULL,
        HoursCredited   DECIMAL(5,2) NOT NULL DEFAULT 8.00,
        CampusId        INT NULL,
        AppliesToAllCampuses BIT NOT NULL DEFAULT 1,
        SchoolYear      NVARCHAR(9) NOT NULL DEFAULT '2025-2026',
        IsActive        BIT NOT NULL DEFAULT 1,
        CreatedBy       NVARCHAR(100) NULL,
        CreatedDate     DATETIME2 NOT NULL DEFAULT GETDATE(),
        ModifiedDate    DATETIME2 NOT NULL DEFAULT GETDATE(),

        CONSTRAINT FK_HolidaySchedule_Campus
            FOREIGN KEY (CampusId) REFERENCES Attendance_Campuses(CampusId)
    );

    CREATE INDEX IX_TC_HolidaySchedule_Date ON TC_HolidaySchedule(HolidayDate);
    CREATE INDEX IX_TC_HolidaySchedule_SchoolYear ON TC_HolidaySchedule(SchoolYear);
END
