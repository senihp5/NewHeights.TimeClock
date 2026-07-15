namespace NewHeights.TimeClock.Shared.Enums;

/// <summary>
/// How a TC_ClassAttendance row was created. Drives audit trail and
/// whether the row carries a meaningful ScannedAt timestamp.
///
///   TeacherManual    - teacher tapped a status pill on the roster grid
///   QrPhone          - student scanned a posted classroom QR with their phone (Phase E)
///   ClassroomKiosk   - ESP32 classroom kiosk scan (Phase F+ / v2 firmware)
///   Inferred         - derived from existing campus-checkin scan + bell period match
///   BulkPresent      - teacher hit "default all to Present" on the grid
///
/// Stored as a string in SQL via HasConversion&lt;string&gt;().
/// </summary>
public enum ClassAttendanceSource
{
    TeacherManual,
    QrPhone,
    ClassroomKiosk,
    Inferred,
    BulkPresent
}
