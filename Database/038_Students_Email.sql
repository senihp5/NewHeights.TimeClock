-- =============================================================================
-- Migration 038: Phase 8 — Add Email column to Students
-- Date: 2026-04-21 (Phase 8 repair)
-- Idempotent — safe to re-run.
--
-- Phase 8 added the Email property to Student.cs and mapped it in
-- TimeClockDbContext (NVARCHAR(200)) but never wrote the ALTER TABLE.
-- Result: /student/checkin crashed on the first query against Students.Email.
-- This migration closes that gap.
--
-- Column is nullable because the Students table is populated by PowerSchool
-- sync / IDSuite3; emails are seeded when school Google accounts are issued.
-- Rows without emails simply cannot sign in until populated — that's the
-- intended degradation.
-- =============================================================================

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.Students')
      AND name = 'Email'
)
BEGIN
    ALTER TABLE dbo.Students
        ADD Email NVARCHAR(200) NULL;
END
GO

-- Filtered non-unique index. Supports the StudentCheckin.razor lookup:
--   Students WHERE Email = @signedInEmail AND IsActive = 1
-- Filtered on non-null + active so inactive / emailless rows don't bloat it.
-- Note: IdNumber is the physical column; Student.StudentNumber is the C#
-- property name mapped via .HasColumnName("IdNumber") in the DbContext.
IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = 'IX_Students_Email_Active'
      AND object_id = OBJECT_ID('dbo.Students')
)
BEGIN
    CREATE INDEX IX_Students_Email_Active
        ON dbo.Students (Email)
        INCLUDE (IsActive, IdNumber, FirstName, LastName, Grade)
        WHERE Email IS NOT NULL AND IsActive = 1;
END
GO
