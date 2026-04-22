-- Migration 046: Replace "Admissions" seed specialty with "Reception" + "Admin"
-- Date: 2026-04-21 (session 3)
-- Purpose: Migration 045's initial seed was "Admissions" as the only catalog
--          entry. Product direction shifted after a smoke-test pass: the
--          initial catalog should be "Reception" and "Admin", extensible
--          from here. Admissions is deactivated rather than deleted so any
--          existing TC_SubSpecialtyAssignment rows remain queryable in
--          audit history (ON DELETE CASCADE would wipe them).
--
-- Idempotent: safe to re-run. GO separators between blocks per the migration
-- 042 lesson (column refs in subsequent statements need the parser to have
-- seen the prior CREATE).

-- ── Step 1: Seed Reception ───────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM TC_SubSpecialty WHERE SpecialtyName = 'Reception')
BEGIN
    INSERT INTO TC_SubSpecialty (SpecialtyName, Description, SortOrder, IsActive)
    VALUES ('Reception', 'Front-desk reception coverage', 10, 1);
    PRINT 'Seeded TC_SubSpecialty: Reception';
END
ELSE
BEGIN
    -- Re-activate if it existed but was previously deactivated.
    UPDATE TC_SubSpecialty
       SET IsActive = 1, ModifiedDate = GETDATE()
     WHERE SpecialtyName = 'Reception' AND IsActive = 0;
    PRINT 'Skip: Reception already exists (re-activated if needed).';
END
GO

-- ── Step 2: Seed Admin ───────────────────────────────────────────────

IF NOT EXISTS (SELECT 1 FROM TC_SubSpecialty WHERE SpecialtyName = 'Admin')
BEGIN
    INSERT INTO TC_SubSpecialty (SpecialtyName, Description, SortOrder, IsActive)
    VALUES ('Admin', 'General administrative coverage', 20, 1);
    PRINT 'Seeded TC_SubSpecialty: Admin';
END
ELSE
BEGIN
    UPDATE TC_SubSpecialty
       SET IsActive = 1, ModifiedDate = GETDATE()
     WHERE SpecialtyName = 'Admin' AND IsActive = 0;
    PRINT 'Skip: Admin already exists (re-activated if needed).';
END
GO

-- ── Step 3: Deactivate Admissions (keep row for audit history) ───────

IF EXISTS (SELECT 1 FROM TC_SubSpecialty WHERE SpecialtyName = 'Admissions' AND IsActive = 1)
BEGIN
    UPDATE TC_SubSpecialty
       SET IsActive = 0, ModifiedDate = GETDATE()
     WHERE SpecialtyName = 'Admissions';
    PRINT 'Deactivated TC_SubSpecialty: Admissions (existing assignments preserved)';
END
ELSE PRINT 'Skip: Admissions not active or not found.';
GO

PRINT 'Migration 046 complete.';
