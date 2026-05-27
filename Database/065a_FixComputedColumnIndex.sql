-- =====================================================================
-- Migration 065a: Recreate IX_TC_ClassAttendance_PsCode_Date with
--                 a non-computed-column filter
-- =====================================================================
-- Purpose:
--   Migration 065 tried to create a filtered index with the predicate
--   [PsAttendanceCode] <> '' . SQL Server rejects this with Msg 10609
--   because filter expressions cannot reference computed columns,
--   even when the computed column is PERSISTED.
--
--   The semantically equivalent filter on the source Status column is
--   [Status] IN ('Tardy', 'Absent', 'Excused') - exactly the set of
--   rows where the computed PsAttendanceCode is non-empty.
--
--   The other 5 indexes created by 065 are fine; only this one needs
--   to be created here.
--
-- Rollback:
--   DROP INDEX [IX_TC_ClassAttendance_PsCode_Date] ON [dbo].[TC_ClassAttendance];
-- =====================================================================

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = 'IX_TC_ClassAttendance_PsCode_Date'
      AND object_id = OBJECT_ID('[dbo].[TC_ClassAttendance]')
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_TC_ClassAttendance_PsCode_Date]
        ON [dbo].[TC_ClassAttendance] ([AttendanceDate], [PsAttendanceCode])
        INCLUDE ([ClassSectionId], [StudentDcid])
        WHERE [Status] IN ('Tardy', 'Absent', 'Excused');

    PRINT 'Created IX_TC_ClassAttendance_PsCode_Date with corrected filter';
END
ELSE
BEGIN
    PRINT 'IX_TC_ClassAttendance_PsCode_Date already exists - skipping';
END
GO

SELECT
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    i.has_filter AS HasFilter,
    i.filter_definition AS FilterDef
FROM sys.indexes i
WHERE i.object_id = OBJECT_ID('[dbo].[TC_ClassAttendance]')
  AND i.type > 0
ORDER BY i.name;
