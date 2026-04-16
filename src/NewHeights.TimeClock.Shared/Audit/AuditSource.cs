namespace NewHeights.TimeClock.Shared.Audit;

/// <summary>
/// Constants for the Source column on TC_AuditLog. Indicates where the audited
/// action originated — physical kiosk, mobile device, admin page, background
/// service, external API, or public unauthenticated link (sub accept/decline).
/// Column is NVARCHAR(15) — keep values short.
/// </summary>
public static class AuditSource
{
    public const string Kiosk       = "KIOSK";
    public const string Mobile      = "MOBILE";
    public const string AdminUi     = "ADMIN_UI";
    public const string ReceptionUi = "RECEPTION_UI";
    public const string System      = "SYSTEM";
    public const string Api         = "API";
    public const string PublicLink  = "PUBLIC_LINK";
}
