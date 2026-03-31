-- ============================================================
-- Migration 005: Create TC_MasterSchedule table
-- Purpose: Stores the resolved campus master schedule —
--          teacher assignments per period, per day pattern,
--          per term. Teacher names are matched to Staff records
--          during import and stored as StaffDcid references.
--
-- Import workflow:
--   1. Admin uploads spreadsheet at /admin/schedule/import
--   2. System parses teacher name tokens, fuzzy-matches to Staff
--   3. Admin reviews/corrects matches in the UI
--   4. On confirm, rows are written here
--   5. EmployeeSyncService reads this table to set Shift on TcEmployee
--
-- Run: SSMS against IDCardPrinterDB (or IDSuite3 DB)
-- Safe: Idempotent — checks table existence before creating
-- ============================================================

IF NOT EXISTS (
    SELECT 1 FROM INFORMATION_SCHEMA.TABLES
    WHERE TABLE_NAME = 'TC_MasterSchedule'
)
BEGIN
    CREATE TABLE TC_MasterSchedule (
        ScheduleId          INT IDENTITY(1,1) PRIMARY KEY,

        -- Context
        CampusId            INT NOT NULL,
        TermName            NVARCHAR(20) NOT NULL,      -- 'TERM2', 'TERM3', 'TERM4'
        SchoolYear          NVARCHAR(9)  NOT NULL,      -- '2025-26'

        -- Raw import data (preserved for audit / re-import)
        RawTeacherCell      NVARCHAR(200) NULL,         -- original cell value verbatim
        RawPartnerNames     NVARCHAR(200) NULL,         -- extracted partner name(s) verbatim

        -- Resolved staff references (NULL until matched)
        TeacherStaffDcid    INT NULL,                   -- FK -> Staff.Dcid (teacher of record)
        Partner1StaffDcid   INT NULL,                   -- FK -> Staff.Dcid (first partner / n/a)
        Partner2StaffDcid   INT NULL,                   -- FK -> Staff.Dcid (second partner, when / used)

        -- Schedule metadata
        Room                NVARCHAR(10)  NULL,
        DayPattern          NVARCHAR(10)  NOT NULL,     -- 'DAY', 'MW', 'TTH', 'DAY/TTH'
        Shift               NVARCHAR(10)  NOT NULL,     -- 'DAY', 'EVENING'
        ContentArea         NVARCHAR(20)  NULL,         -- 'CTE', 'MATH', 'ELAR', 'SCI', 'SS'

        -- Mon/Wed periods (NULL = not scheduled that day/period)
        MW_P1               NVARCHAR(100) NULL,
        MW_P2               NVARCHAR(100) NULL,
        MW_P3               NVARCHAR(100) NULL,
        MW_P4               NVARCHAR(100) NULL,
        MW_P5               NVARCHAR(100) NULL,
        MW_P6               NVARCHAR(100) NULL,

        -- Tue/Thu periods
        TTh_P1              NVARCHAR(100) NULL,
        TTh_P2              NVARCHAR(100) NULL,
        TTh_P3              NVARCHAR(100) NULL,
        TTh_P4              NVARCHAR(100) NULL,
        TTh_P5              NVARCHAR(100) NULL,
        TTh_P6              NVARCHAR(100) NULL,

        -- Match confidence from import
        -- 'Exact', 'LastName', 'Fuzzy', 'Manual', 'Unmatched'
        TeacherMatchMethod  NVARCHAR(20)  NULL,
        Partner1MatchMethod NVARCHAR(20)  NULL,
        Partner2MatchMethod NVARCHAR(20)  NULL,

        -- Record lifecycle
        IsActive            BIT NOT NULL DEFAULT 1,
        ImportedDate        DATETIME NOT NULL DEFAULT GETUTCDATE(),
        ImportedBy          NVARCHAR(150) NULL,
        Notes               NVARCHAR(500) NULL
    );

    -- Indexes for common lookups
    CREATE INDEX IX_TC_MasterSchedule_Campus_Term
        ON TC_MasterSchedule (CampusId, TermName, SchoolYear);

    CREATE INDEX IX_TC_MasterSchedule_TeacherDcid
        ON TC_MasterSchedule (TeacherStaffDcid)
        WHERE TeacherStaffDcid IS NOT NULL;

    CREATE INDEX IX_TC_MasterSchedule_Partner1Dcid
        ON TC_MasterSchedule (Partner1StaffDcid)
        WHERE Partner1StaffDcid IS NOT NULL;

    CREATE INDEX IX_TC_MasterSchedule_Partner2Dcid
        ON TC_MasterSchedule (Partner2StaffDcid)
        WHERE Partner2StaffDcid IS NOT NULL;

    PRINT 'Table TC_MasterSchedule created with indexes.';
END
ELSE
BEGIN
    PRINT 'Table TC_MasterSchedule already exists — skipped.';
END
GO

-- Verify
SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT
FROM   INFORMATION_SCHEMA.COLUMNS
WHERE  TABLE_NAME = 'TC_MasterSchedule'
ORDER  BY ORDINAL_POSITION;
GO
