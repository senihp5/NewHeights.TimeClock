-- Migration 047: Rename SubRole values on TC_Employees
-- Date: 2026-04-21 (session 3b)
-- Purpose: Product direction: SubRole identifies the kind of sub, not a
--          specialty. New values:
--              RECEPTION → ADMIN_SUB   (reception is now a specialty,
--                                        not a role — stored in
--                                        TC_SubSpecialty + TC_SubSpecialtyAssignment)
--              TEACHER   → TEACHER_SUB (align naming)
--              NULL      → unchanged. Legacy NULL is interpreted by the
--                          application as "TEACHER_SUB by default" for
--                          EmployeeType=Substitute rows.
--
-- NOTE: TC_SubRequests.RequestType still uses 'RECEPTION'/'TEACHER'. Those
-- values describe the kind of absence request (reception-desk coverage vs.
-- classroom coverage), not the employee role, so they stay. Outreach logic
-- now maps RequestType='RECEPTION' → SubRole='ADMIN_SUB' candidates.
--
-- Idempotent: WHERE clauses ensure re-run does nothing.

IF EXISTS (SELECT 1 FROM TC_Employees WHERE SubRole = 'RECEPTION')
BEGIN
    UPDATE TC_Employees
       SET SubRole = 'ADMIN_SUB',
           ModifiedDate = GETDATE()
     WHERE SubRole = 'RECEPTION';
    PRINT CONCAT('Renamed ', @@ROWCOUNT, ' rows: SubRole RECEPTION -> ADMIN_SUB');
END
ELSE PRINT 'Skip: no rows with SubRole=RECEPTION.';
GO

IF EXISTS (SELECT 1 FROM TC_Employees WHERE SubRole = 'TEACHER')
BEGIN
    UPDATE TC_Employees
       SET SubRole = 'TEACHER_SUB',
           ModifiedDate = GETDATE()
     WHERE SubRole = 'TEACHER';
    PRINT CONCAT('Renamed ', @@ROWCOUNT, ' rows: SubRole TEACHER -> TEACHER_SUB');
END
ELSE PRINT 'Skip: no rows with SubRole=TEACHER.';
GO

PRINT 'Migration 047 complete.';
