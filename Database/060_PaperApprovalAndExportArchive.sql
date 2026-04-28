/*
===============================================================================
Migration 060 — Paper-approval audit fields + payroll-export blob archive
Date: 2026-04-27
Purpose:
  1. Add EmployeeApprovedBy + EmployeeApprovedDate to TC_PayPeriodSummary so
     paper-approval workflow can stamp the employee stage with a distinct
     identity (matching the existing SupervisorApprovedBy and HRApprovedBy
     pattern). Without these columns the audit log is the only place to see
     when the employee submitted, which makes recovery harder.

  2. Add CsvBlob + PdfBlob (VARBINARY(MAX)) to TC_PayrollExports so the
     exact bytes of the CSV / PDF generated at export time are stored in
     the database for historical recovery. Today the exports only live on
     whatever device hit "Export" — replaying a payroll period is then
     impossible without rebuilding the file from raw data, which can drift
     if any row was edited after the original export.

  Idempotent.
===============================================================================
*/
SET NOCOUNT ON;
GO

PRINT '========================================';
PRINT 'Migration 060: Paper-approval fields + export blob archive';
PRINT 'Started: ' + CONVERT(VARCHAR, GETDATE(), 120);
PRINT '========================================';

-- TC_PayPeriodSummary: EmployeeApprovedBy / EmployeeApprovedDate -------------

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_PayPeriodSummary')
      AND name = 'EmployeeApprovedBy'
)
BEGIN
    ALTER TABLE TC_PayPeriodSummary ADD EmployeeApprovedBy NVARCHAR(100) NULL;
    PRINT '    Added column: TC_PayPeriodSummary.EmployeeApprovedBy';
END
ELSE PRINT '    Skipped: TC_PayPeriodSummary.EmployeeApprovedBy already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_PayPeriodSummary')
      AND name = 'EmployeeApprovedDate'
)
BEGIN
    ALTER TABLE TC_PayPeriodSummary ADD EmployeeApprovedDate DATETIME NULL;
    PRINT '    Added column: TC_PayPeriodSummary.EmployeeApprovedDate';
END
ELSE PRINT '    Skipped: TC_PayPeriodSummary.EmployeeApprovedDate already exists';
GO

-- TC_PayrollExports: blob columns for CSV + PDF historical recovery -----------

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_PayrollExports')
      AND name = 'CsvBlob'
)
BEGIN
    ALTER TABLE TC_PayrollExports ADD CsvBlob VARBINARY(MAX) NULL;
    PRINT '    Added column: TC_PayrollExports.CsvBlob';
END
ELSE PRINT '    Skipped: TC_PayrollExports.CsvBlob already exists';
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('TC_PayrollExports')
      AND name = 'PdfBlob'
)
BEGIN
    ALTER TABLE TC_PayrollExports ADD PdfBlob VARBINARY(MAX) NULL;
    PRINT '    Added column: TC_PayrollExports.PdfBlob';
END
ELSE PRINT '    Skipped: TC_PayrollExports.PdfBlob already exists';
GO

PRINT 'Migration 060 complete.';
GO
