-- Migration 036: TC_AuditLog default fix (local time) + indexes for 65-action workload
-- Date: 2026-04-16
-- Purpose: Critical Rule 4.1 compliance (all writes local time, never UTC).
--          Migration 002 created TC_AuditLog with CreatedDate default GETUTCDATE().
--          Change default to SYSDATETIME() (local server time) to match DateTime.Now writes.
--          Add composite (EmployeeId, CreatedDate) index for per-employee history lookups.
--          Add Source index for compliance reporting by origin (KIOSK / ADMIN_UI / SYSTEM / PUBLIC_LINK).

-- 036a: Drop old CreatedDate default, add new local-time default
IF EXISTS (
    SELECT 1 FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE c.object_id = OBJECT_ID('TC_AuditLog') AND c.name = 'CreatedDate'
      AND OBJECT_DEFINITION(dc.object_id) LIKE '%getutcdate%'
)
BEGIN
    DECLARE @auditDefaultName NVARCHAR(200);
    SELECT @auditDefaultName = dc.name FROM sys.default_constraints dc
    INNER JOIN sys.columns c ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE c.object_id = OBJECT_ID('TC_AuditLog') AND c.name = 'CreatedDate';

    EXEC('ALTER TABLE TC_AuditLog DROP CONSTRAINT ' + @auditDefaultName);
    ALTER TABLE TC_AuditLog ADD CONSTRAINT DF_TCAudit_CreatedDate DEFAULT SYSDATETIME() FOR CreatedDate;
    PRINT 'Migration 036a: Replaced TC_AuditLog.CreatedDate default with SYSDATETIME() (local time).';
END
ELSE
BEGIN
    PRINT 'Migration 036a: TC_AuditLog.CreatedDate default already local. Skipped.';
END

-- 036b: Per-employee chronological index
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TC_AuditLog') AND name = 'IX_TCAudit_EmployeeDate')
BEGIN
    CREATE INDEX IX_TCAudit_EmployeeDate ON TC_AuditLog(EmployeeId, CreatedDate DESC) WHERE EmployeeId IS NOT NULL;
    PRINT 'Migration 036b: Created IX_TCAudit_EmployeeDate.';
END
ELSE
BEGIN
    PRINT 'Migration 036b: IX_TCAudit_EmployeeDate already exists. Skipped.';
END

-- 036c: Source index for compliance reports
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TC_AuditLog') AND name = 'IX_TCAudit_Source')
BEGIN
    CREATE INDEX IX_TCAudit_Source ON TC_AuditLog(Source);
    PRINT 'Migration 036c: Created IX_TCAudit_Source.';
END
ELSE
BEGIN
    PRINT 'Migration 036c: IX_TCAudit_Source already exists. Skipped.';
END

-- 036d: ActionCode + CreatedDate composite for time-windowed action queries
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('TC_AuditLog') AND name = 'IX_TCAudit_ActionCodeDate')
BEGIN
    CREATE INDEX IX_TCAudit_ActionCodeDate ON TC_AuditLog(ActionCode, CreatedDate DESC);
    PRINT 'Migration 036d: Created IX_TCAudit_ActionCodeDate.';
END
ELSE
BEGIN
    PRINT 'Migration 036d: IX_TCAudit_ActionCodeDate already exists. Skipped.';
END

PRINT 'Migration 036 complete.';
