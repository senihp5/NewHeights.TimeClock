SET NOCOUNT ON;
SET XACT_ABORT ON;

BEGIN TRANSACTION;

IF OBJECT_ID('tempdb..#ToVoid') IS NOT NULL DROP TABLE #ToVoid;

;WITH candidates AS (
    SELECT p.PunchId,
           p.EmployeeId,
           p.PunchType,
           p.PunchDateTime,
           p.IsAutoCheckout,
           p.PairedPunchId,
           CONVERT(date, p.PunchDateTime) AS WorkDate,
           CASE WHEN p.IsAutoCheckout = 1 THEN 'LEGACY_AUTOOUT_DUPE'
                ELSE 'BADGE_NEAR_MANUAL' END AS MatchRule
    FROM TC_TimePunches p
    WHERE p.PunchStatus = 'Active'
      AND (
            (p.IsAutoCheckout = 1 AND p.PunchDateTime < '2026-04-22'
             AND EXISTS (SELECT 1 FROM TC_TimePunches b
                         WHERE b.EmployeeId = p.EmployeeId
                           AND b.PunchType = 'Out'
                           AND b.PunchStatus = 'Active'
                           AND b.PunchId <> p.PunchId
                           AND CONVERT(date, b.PunchDateTime) = CONVERT(date, p.PunchDateTime)))
         OR
            (p.IsAutoCheckout = 0 AND p.IsManualEntry = 0 AND p.PunchType IN ('In', 'Out')
             AND EXISTS (SELECT 1 FROM TC_TimePunches m
                         WHERE m.EmployeeId = p.EmployeeId
                           AND m.PunchType = p.PunchType
                           AND m.PunchStatus = 'Active'
                           AND m.IsManualEntry = 1
                           AND ABS(DATEDIFF(MINUTE, m.PunchDateTime, p.PunchDateTime)) <= 5
                           AND m.PunchId <> p.PunchId))
          )
)
SELECT * INTO #ToVoid FROM candidates;

DECLARE @ToVoidCount INT = (SELECT COUNT(*) FROM #ToVoid);
PRINT CONCAT('Punches queued to void: ', @ToVoidCount);

IF @ToVoidCount = 0
BEGIN
    PRINT 'Nothing to do. Rolling back.';
    ROLLBACK TRANSACTION;
    RETURN;
END;

UPDATE p
SET PunchStatus    = 'Voided',
    IsModified     = 1,
    ModifiedBy     = 'SYSTEM_BULK_CLEANUP',
    ModifiedReason = '[Bulk Cleanup 2026-04-28] Legacy auto-out / badge-near-manual duplicate of IMPORT manual entry'
FROM TC_TimePunches p
JOIN #ToVoid v ON v.PunchId = p.PunchId;

PRINT CONCAT('TC_TimePunches rows voided: ', @@ROWCOUNT);

UPDATE p
SET PairedPunchId = NULL
FROM TC_TimePunches p
JOIN #ToVoid v ON v.PunchId = p.PunchId
WHERE p.PairedPunchId IS NOT NULL;

PRINT CONCAT('PairedPunchId cleared on voided rows: ', @@ROWCOUNT);

UPDATE p
SET PairedPunchId = NULL
FROM TC_TimePunches p
WHERE p.PunchStatus = 'Active'
  AND p.PairedPunchId IS NOT NULL
  AND p.PairedPunchId IN (SELECT PunchId FROM #ToVoid);

PRINT CONCAT('PairedPunchId cleared on surviving partners: ', @@ROWCOUNT);

INSERT INTO TC_AuditLog (
    ActionCode, UserId, UserName, UserEmail, UserRole,
    EntityType, EntityId, PunchId, CorrectionId, EmployeeId, CampusId,
    OldValuesJson, NewValuesJson, DeltaSummary, Reason,
    Source, IPAddress, SessionId, CreatedDate
)
SELECT
    'PUNCH_MODIFIED',
    'SYSTEM_BULK_CLEANUP',
    'SYSTEM_BULK_CLEANUP',
    NULL,
    NULL,
    'PUNCH',
    CAST(v.PunchId AS NVARCHAR(50)),
    v.PunchId,
    NULL,
    v.EmployeeId,
    NULL,
    '{"PunchStatus":"Active"}',
    CONCAT(
        '{"PunchStatus":"Voided","PunchType":"', v.PunchType,
        '","PunchDateTime":"', CONVERT(NVARCHAR(33), v.PunchDateTime, 126),
        '","IsAutoCheckout":', CASE WHEN v.IsAutoCheckout = 1 THEN 'true' ELSE 'false' END,
        ',"VoidedBy":"SYSTEM_BULK_CLEANUP","Source":"SYSTEM_BULK_CLEANUP","MatchRule":"', v.MatchRule, '"}'
    ),
    CONCAT('Bulk cleanup voided ', v.PunchType, ' at ',
           CONVERT(NVARCHAR(20), v.PunchDateTime, 100),
           ' for employee ', v.EmployeeId, ' (rule: ', v.MatchRule, ')'),
    '[Bulk Cleanup 2026-04-28] Legacy auto-out / badge-near-manual duplicate of IMPORT manual entry',
    'SYSTEM',
    NULL,
    NULL,
    GETDATE()
FROM #ToVoid v;

PRINT CONCAT('TC_AuditLog rows inserted: ', @@ROWCOUNT);

PRINT '';
PRINT '=== Recalc list ((EmployeeId, WorkDate) pairs needing RecalculateDailyTimecardAsync) ===';

SELECT DISTINCT
    EmployeeId,
    WorkDate
FROM #ToVoid
ORDER BY WorkDate, EmployeeId;

PRINT '';
PRINT '======================================================================';
PRINT 'DRY RUN COMPLETE. Review counts + recalc list above.';
PRINT 'If correct, change the bottom of the script to COMMIT and re-run.';
PRINT '======================================================================';

-- ROLLBACK TRANSACTION;
COMMIT TRANSACTION;