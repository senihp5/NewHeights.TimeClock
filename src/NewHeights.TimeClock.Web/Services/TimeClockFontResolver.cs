using System.Reflection;
using PdfSharp.Fonts;

namespace NewHeights.TimeClock.Web.Services;

/// <summary>
/// PDFsharp 6 font resolver for the Linux App Service. PdfSharpCore shipped a
/// default resolver; PDFsharp 6 Core does not, so we serve a bundled,
/// Arial-metric-compatible face (Liberation Sans) for every request. The two
/// TTFs are embedded resources under Assets/Fonts/.
/// </summary>
public sealed class TimeClockFontResolver : IFontResolver
{
    private const string RegularFace = "LiberationSans#Regular";
    private const string BoldFace = "LiberationSans#Bold";

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic)
        => new FontResolverInfo(isBold ? BoldFace : RegularFace);

    public byte[] GetFont(string faceName)
    {
        var resource = faceName == BoldFace
            ? "NewHeights.TimeClock.Web.Assets.Fonts.LiberationSans-Bold.ttf"
            : "NewHeights.TimeClock.Web.Assets.Fonts.LiberationSans-Regular.ttf";

        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded font not found: {resource}");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
