-- =====================================================================
-- Migration 069: Add PhotoCaptureEnabled column to TC_Terminals
-- =====================================================================
-- Purpose:
--   Phase 1 of tablet-kiosk build pack (2026-06-30 / 2026-07-08). Adds a
--   per-terminal opt-in flag for front-camera photo capture at scan time.
--
--   Rear-camera QR scanning is always on for tablet kiosks. Front-camera
--   photo capture is plumbed in the tablet page's JS + C# code paths but
--   disabled by default everywhere — a specific terminal can be opted in
--   later by flipping this flag to 1 via /admin/kiosks.
--
--   Existing ESP32 kiosks are unaffected. Their firmware handles its own
--   photo capture path; this flag is only read by KioskTablet.razor (Phase
--   2) to decide whether to open the front camera in addition to the rear.
--
-- Anchors:
--   - Column added to TC_Terminals (created migration 061).
--   - Read by KioskTablet.razor at page init (Phase 2, forthcoming).
--   - Editable via KioskTerminals.razor admin page (Phase 1).
--
-- Rollback:
--   ALTER TABLE [dbo].[TC_Terminals] DROP COLUMN [PhotoCaptureEnabled];
-- =====================================================================

IF NOT EXISTS (
    SELECT 1
    FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.TC_Terminals')
      AND name = 'PhotoCaptureEnabled'
)
BEGIN
    ALTER TABLE [dbo].[TC_Terminals]
        ADD [PhotoCaptureEnabled] BIT NOT NULL DEFAULT 0;

    PRINT 'Added PhotoCaptureEnabled column to TC_Terminals (default 0 = disabled)';
END
ELSE
BEGIN
    PRINT 'PhotoCaptureEnabled column already exists on TC_Terminals - skipping add';
END
GO

SELECT TerminalId, TerminalCode, DeviceType, TerminalPurpose, IsActive, PhotoCaptureEnabled
FROM [dbo].[TC_Terminals]
ORDER BY TerminalId;
