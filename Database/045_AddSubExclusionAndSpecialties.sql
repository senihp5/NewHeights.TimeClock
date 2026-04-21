-- Migration 045: Phase D5 — sub pool exclusion + admin-sub structured specialties
-- Date: 2026-04-21
-- Purpose: Three concerns bundled in one migration because the entities all
--          ride on TC_Employees:
--   1. Exclusion list — supervisors can mark a specific sub "do not contact"
--      without removing them from the Entra group. Adds 4 columns to
--      TC_Employees so the audit trail of who-excluded-when-why lives with
--      the row.
--   2. Specialty catalog — structured list of admin-sub specialties (seed:
--      "Admissions"). Future entries added by HR via the admin page.
--      Specialties only apply to RECEPTION subs, not teacher subs.
--   3. Specialty assignments — many-to-many between admin subs and the
--      catalog rows.
--
-- GO separators between blocks (per the migration 042 lesson — column refs
-- in CREATE INDEX WHERE clauses need the column to exist in the parsed
-- schema).

-- ── Step 1: Exclusion columns on TC_Employees ────────────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE Name = N'ExcludedFromSubPool'
      AND Object_ID = Object_ID(N'TC_Employees')
)
BEGIN
    ALTER TABLE TC_Employees
        ADD ExcludedFromSubPool       BIT            NOT NULL DEFAULT 0,
            ExcludedFromSubPoolBy     NVARCHAR(100)  NULL,
            ExcludedFromSubPoolDate   DATETIME       NULL,
            ExcludedFromSubPoolReason NVARCHAR(500)  NULL;

    PRINT 'Added TC_Employees exclusion columns';
END
ELSE PRINT 'Skip: TC_Employees.ExcludedFromSubPool already exists.';
GO

-- ── Step 2: Filtered index for excluded subs ─────────────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TC_Employees_ExcludedFromSubPool'
      AND object_id = OBJECT_ID('TC_Employees')
)
BEGIN
    CREATE NONCLUSTERED INDEX IX_TC_Employees_ExcludedFromSubPool
        ON TC_Employees (ExcludedFromSubPool)
        WHERE ExcludedFromSubPool = 1;

    PRINT 'Added IX_TC_Employees_ExcludedFromSubPool';
END
ELSE PRINT 'Skip: IX_TC_Employees_ExcludedFromSubPool already exists.';
GO

-- ── Step 3: TC_SubSpecialty catalog ──────────────────────────────────

IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = N'TC_SubSpecialty'
)
BEGIN
    CREATE TABLE TC_SubSpecialty (
        SpecialtyId   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        SpecialtyName NVARCHAR(50)      NOT NULL UNIQUE,
        Description   NVARCHAR(200)     NULL,
        IsActive      BIT               NOT NULL DEFAULT 1,
        SortOrder     INT               NOT NULL DEFAULT 100,
        CreatedDate   DATETIME          NOT NULL DEFAULT GETDATE(),
        ModifiedDate  DATETIME          NOT NULL DEFAULT GETDATE()
    );

    PRINT 'Created TC_SubSpecialty';
END
ELSE PRINT 'Skip: TC_SubSpecialty already exists.';
GO

-- ── Step 4: Seed initial specialty (Admissions) ──────────────────────

IF NOT EXISTS (
    SELECT 1 FROM TC_SubSpecialty WHERE SpecialtyName = 'Admissions'
)
BEGIN
    INSERT INTO TC_SubSpecialty (SpecialtyName, Description, SortOrder, IsActive)
    VALUES ('Admissions', 'Admissions office front-desk coverage', 10, 1);

    PRINT 'Seeded TC_SubSpecialty: Admissions';
END
ELSE PRINT 'Skip: Admissions already seeded.';
GO

-- ── Step 5: TC_SubSpecialtyAssignment (sub ↔ specialty M-to-M) ───────

IF NOT EXISTS (
    SELECT 1 FROM sys.tables WHERE name = N'TC_SubSpecialtyAssignment'
)
BEGIN
    CREATE TABLE TC_SubSpecialtyAssignment (
        AssignmentId  BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        EmployeeId    INT             NOT NULL,
        SpecialtyId   INT             NOT NULL,
        AssignedBy    NVARCHAR(100)   NULL,
        AssignedDate  DATETIME        NOT NULL DEFAULT GETDATE(),
        CONSTRAINT FK_TC_SubSpecialtyAssignment_Employee
            FOREIGN KEY (EmployeeId) REFERENCES TC_Employees(EmployeeId)
            ON DELETE CASCADE,
        CONSTRAINT FK_TC_SubSpecialtyAssignment_Specialty
            FOREIGN KEY (SpecialtyId) REFERENCES TC_SubSpecialty(SpecialtyId)
            ON DELETE CASCADE,
        CONSTRAINT UX_TC_SubSpecialtyAssignment_EmpSpec
            UNIQUE (EmployeeId, SpecialtyId)
    );

    PRINT 'Created TC_SubSpecialtyAssignment';
END
ELSE PRINT 'Skip: TC_SubSpecialtyAssignment already exists.';
GO

PRINT 'Migration 045 complete.';
