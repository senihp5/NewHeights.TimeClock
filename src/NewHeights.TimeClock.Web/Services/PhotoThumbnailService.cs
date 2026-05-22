using SkiaSharp;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// Produces small JPEG thumbnails from a base64-encoded source image.
/// Currently used by the /api/v1/punch endpoint so ESP32 kiosks receive a
/// 200x200 photo (~10-20 KB base64) instead of the full ~500 KB+ source
/// — see [[reference-kiosk-scan-result-dual-consumer]] memory and the
/// "ESP32 Kiosk Phase 6 Display + Photo" session handoff dated 2026-05-19.
/// </summary>
public interface IPhotoThumbnailService
{
    /// <summary>
    /// Resize <paramref name="sourceBase64"/> to fit within
    /// <paramref name="maxDimensionPx"/> on the longest side (preserving
    /// aspect ratio) and re-encode as JPEG. Returns the resulting base64
    /// string (no data: URI prefix), or null if the input is null/empty
    /// or cannot be decoded.
    /// </summary>
    string? CreateJpegThumbnailBase64(string? sourceBase64, int maxDimensionPx = 200, int qualityPercent = 75);
}

public sealed class PhotoThumbnailService : IPhotoThumbnailService
{
    private readonly ILogger<PhotoThumbnailService> _logger;

    public PhotoThumbnailService(ILogger<PhotoThumbnailService> logger)
    {
        _logger = logger;
    }

    public string? CreateJpegThumbnailBase64(string? sourceBase64, int maxDimensionPx = 200, int qualityPercent = 75)
    {
        if (string.IsNullOrEmpty(sourceBase64)) return null;
        if (maxDimensionPx <= 0) return null;
        if (qualityPercent < 1 || qualityPercent > 100) qualityPercent = 75;

        byte[] sourceBytes;
        try
        {
            sourceBytes = Convert.FromBase64String(sourceBase64);
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "PhotoBase64 was not valid base64 — returning null thumbnail");
            return null;
        }

        using var sourceBitmap = SKBitmap.Decode(sourceBytes);
        if (sourceBitmap == null)
        {
            _logger.LogWarning("SKBitmap.Decode returned null for {Bytes}-byte source image", sourceBytes.Length);
            return null;
        }

        int w = sourceBitmap.Width;
        int h = sourceBitmap.Height;
        if (w <= 0 || h <= 0) return null;

        double scale = (double)maxDimensionPx / Math.Max(w, h);

        SKBitmap workBitmap;
        bool disposeWork;

        if (scale >= 1.0)
        {
            workBitmap = sourceBitmap;
            disposeWork = false;
        }
        else
        {
            int newW = Math.Max(1, (int)Math.Round(w * scale));
            int newH = Math.Max(1, (int)Math.Round(h * scale));
            var resized = sourceBitmap.Resize(new SKSizeI(newW, newH), SKFilterQuality.High);
            if (resized == null)
            {
                _logger.LogWarning("SKBitmap.Resize returned null for {W}x{H} -> {NewW}x{NewH}", w, h, newW, newH);
                return null;
            }
            workBitmap = resized;
            disposeWork = true;
        }

        try
        {
            using var image = SKImage.FromBitmap(workBitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, qualityPercent);
            var outBytes = encoded.ToArray();

            _logger.LogDebug("Thumbnail: source {SrcW}x{SrcH} {SrcBytes} bytes -> {DstW}x{DstH} {DstBytes} bytes (q{Quality})",
                w, h, sourceBytes.Length, workBitmap.Width, workBitmap.Height, outBytes.Length, qualityPercent);

            return Convert.ToBase64String(outBytes);
        }
        finally
        {
            if (disposeWork) workBitmap.Dispose();
        }
    }
}
