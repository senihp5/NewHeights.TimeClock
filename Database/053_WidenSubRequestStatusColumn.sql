/*
===============================================================================
Migration 053 — Widen TC_SubRequests.Status column
Date: 2026-04-27
Purpose:
  Migration 048 added 'PartiallyAssigned' (17 chars) to the SubRequestStatus
  enum but never widened the underlying column, which has been NVARCHAR(15)
  since the table was created. Result: any partial-accept on /sub/respond/*
  threw `String or binary data would be truncated` and the sub respond page
  showed a generic "Could not record your acceptance" error.

  Widen Status to NVARCHAR(30) — same convention as TC_AuditLog action codes
  and TC_AuditEntityTypes, gives headroom for any future enum value without
  another schema bump.

  No data migration required; widening a column does not affect existing rows.
  The HasIndex on Status survives the ALTER on Azure SQL / SQL Server 2016+
  without a drop-and-recreate.

Idempotent — checks current column width before altering.
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 053: Widen TC_SubRequests.Status to NVARCHAR(30)';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';

IF EXISTS (
    SELECT 1
    FROM sys.columns c
    JOIN sys.types  t ON t.user_type_id = c.user_type_id
    WHERE c.object_id = OBJECT_ID('TC_SubRequests')
      AND c.name = 'Status'
      AND t.name = 'nvarchar'
      AND c.max_length < 60   -- nvarchar stores 2 bytes per char; <60 means <30 chars
)
BEGIN
    ALTER TABLE TC_SubRequests ALTER COLUMN [Status] NVARCHAR(30) NOT NULL;
    PRINT '    Widened TC_SubRequests.Status to NVARCHAR(30)';
END
ELSE
BEGIN
    PRINT '    Skipped: TC_SubRequests.Status already >= NVARCHAR(30)';
END
GO

PRINT 'Migration 053 complete.';
GO
