using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Broiler.CSS;
using Broiler.Graphics.Rendering;
using Broiler.Graphics.Text;

namespace Broiler.HtmlBridge;

/// <summary>
/// How <see cref="CanvasRenderingContext2D"/> turns its <c>font</c> attribute into the
/// <see cref="BFontStyle"/> text is measured and drawn with, and the text preparation both steps share.
/// </summary>
/// <remarks>
/// Parsing is Broiler.CSS's: the shorthand is expanded by <c>CssStyleEngine.ExpandShorthands</c> (through
/// <see cref="DomBridgeUtils.ExpandCssShorthands"/>) and the size resolved by <see cref="CssLengthParser"/>,
/// so a canvas reads <c>font</c> the way a style sheet does rather than through a scanner of its own.
/// </remarks>
internal static class CanvasFont
{
    /// <summary>
    /// The font a context starts with (<c>10px sans-serif</c>), and the one HTML resolves a relative size
    /// against when the canvas element's own font is not available.
    /// </summary>
    public static readonly BFontStyle Default = new("sans-serif", 10.0);

    /// <summary>
    /// The CSS Fonts system-font keywords, each a complete <c>font</c> value on its own. The expansion
    /// yields nothing for them.
    /// </summary>
    private static readonly HashSet<string> SystemFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        "caption", "icon", "menu", "message-box", "small-caption", "status-bar",
    };

    /// <summary>
    /// HTML's text preparation for <c>fillText</c>, <c>strokeText</c> and <c>measureText</c>: every ASCII
    /// whitespace character becomes a space. It also keeps measuring and drawing on one advance, because
    /// <see cref="BTextMeasurer"/> skips <c>\r</c> and <c>\n</c> while <see cref="BImageRenderer"/> moves
    /// its pen back to the start of the run at them. <c>Replace</c> returns the same string when there is
    /// nothing to replace, so text without such characters is not copied.
    /// </summary>
    public static string PrepareText(string text) =>
        text.Replace('\t', ' ').Replace('\n', ' ').Replace('\f', ' ').Replace('\r', ' ');

    /// <summary>
    /// Resolves a CSS <c>font</c> shorthand to the face and pixel size text is measured and drawn with,
    /// or fails for a value the <c>font</c> setter must ignore.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The expansion is a classifier, not a validator. It yields no <c>font-size</c> or no
    /// <c>font-family</c> when it finds no size, or nothing after the size; it yields <c>inherit</c> for that
    /// CSS-wide keyword; and a negative size is invalid CSS it admits, so that fails here. It knows only the
    /// nine hundreds as numeric weights, so any other weight before the size (<c>450 14px Inter</c>, valid
    /// in CSS Fonts 4) comes back as a unitless size with the real size heading the family. A unitless
    /// size is invalid outside quirks mode, so such a number is read as the weight and the rest expanded
    /// again, and a bare number that is no weight either (<c>12 serif</c>) fails. A system-font keyword
    /// (<c>caption</c>) is accepted and drawn in the default font, because no consumed API resolves one.
    /// </para>
    /// <para>
    /// Known gaps. An absolute-size keyword keeps the 10px default; <c>em</c>, <c>%</c> and the other
    /// font-relative sizes resolve against that default even on a connected canvas, whose computed font
    /// HTML would use; and only the first family in the list is used. Some valid values are ignored,
    /// because the expansion cannot size them: a viewport-relative size (it has no viewport) and a
    /// <c>calc()</c> holding a percentage. And some invalid values are
    /// accepted, because it passes over tokens before the size it does not know (<c>bogus 20px serif</c>)
    /// and takes everything after the size as the family without checking it (<c>20px serif 30px</c>,
    /// <c>20px , serif</c>). Each is pinned by a skipped test in <c>CanvasTextTests</c>.
    /// </para>
    /// </remarks>
    public static bool TryResolve(string value, [NotNullWhen(true)] out BFontStyle? font)
    {
        font = null;
        if (SystemFonts.Contains(value.Trim()))
        {
            font = Default;
            return true;
        }

        if (Expand(value) is not { } longhands || longhands["font-size"] == "inherit")
            return false;

        if (IsNonZeroNumber(longhands["font-size"], out double number))
        {
            // Read as the weight, if it can be one: the only weight, in [1, 1000], with no line-height of its
            // own, and followed by a real size and family.
            if (number is not (>= 1 and <= 1000)
                || longhands["font-weight"] != "normal"
                || longhands["line-height"] != "normal"
                || Expand(longhands["font-family"]) is not { } rest
                || rest["font-weight"] != "normal"
                || rest["font-size"] == "inherit"
                || IsNonZeroNumber(rest["font-size"], out _))
                return false;

            rest["font-weight"] = ((int)Math.Round(number)).ToString(CultureInfo.InvariantCulture);
            if (rest["font-style"] == "normal")
                rest["font-style"] = longhands["font-style"];
            longhands = rest;
        }

        // The expansion admits a size keyword, a percentage, or a length ParseToPixels resolves, so a
        // size that is neither of the last two is a keyword; no consumed API sizes one, and ParseLength
        // would answer 0, so it keeps the default. ParseLength matches units case-sensitively, hence the
        // lowercasing. A viewport unit never reaches it, so neither its thread-static viewport factors
        // nor the DEBUG-only viewport assertion is involved.
        string size = longhands["font-size"].ToLowerInvariant();
        double pixels = size.EndsWith('%') || !double.IsNaN(CssLengthParser.ParseToPixels(size))
            ? CssLengthParser.ParseLength(size, hundredPercent: Default.Size, emFactor: Default.Size)
            : Default.Size;
        if (!double.IsFinite(pixels) || pixels < 0)
            return false;

        string family = longhands["font-family"].Split(',')[0].Trim().Trim('"', '\'');
        // The expansion lowercases both keywords. Graphics picks a bold face at 700 and above.
        BFontWeight weight = longhands["font-weight"] switch
        {
            "bold" or "bolder" => BFontWeight.Bold,
            string numeric when int.TryParse(numeric, NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                => (BFontWeight)n,
            _ => BFontWeight.Normal,
        };
        BFontSlant slant = longhands["font-style"] switch
        {
            "italic" => BFontSlant.Italic,
            "oblique" => BFontSlant.Oblique,
            _ => BFontSlant.Normal,
        };

        font = new BFontStyle(family.Length == 0 ? Default.FamilyName : family, pixels, weight, slant);
        return true;
    }

    /// <summary>
    /// The longhands the expansion gives <paramref name="value"/>, or <see langword="null"/> when it finds no
    /// size or no family. When it finds both it sets every font longhand, so each can be indexed.
    /// </summary>
    private static Dictionary<string, string>? Expand(string value)
    {
        var longhands = new Dictionary<string, string>(StringComparer.Ordinal) { ["font"] = value };
        DomBridgeUtils.ExpandCssShorthands(longhands);
        return longhands.ContainsKey("font-size") && longhands.ContainsKey("font-family") ? longhands : null;
    }

    /// <summary>
    /// Whether the expanded size is a bare number other than zero — which a length may only be when it is
    /// zero, outside quirks mode.
    /// </summary>
    private static bool IsNonZeroNumber(string size, out double number) =>
        double.TryParse(size, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && number != 0;
}
