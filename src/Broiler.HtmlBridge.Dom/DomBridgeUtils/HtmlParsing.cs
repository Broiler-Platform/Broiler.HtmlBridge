using System.Text.RegularExpressions;
using Broiler.CSS.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static readonly System.Text.RegularExpressions.Regex DocTypePattern = DocTypePatternRegex();

    /// <summary>
    /// Parses a CSS inline style string (e.g. <c>"color: red; font-size: 12px"</c>)
    /// into a property→value dictionary. Implements CSS error recovery: when the
    /// same property is declared multiple times, invalid values are discarded so
    /// the last <em>valid</em> value wins (per CSS 2.1 §4.2 / CSS Syntax §5).
    /// </summary>
    /// <param name="reportDrops">
    /// When <c>true</c>, declarations rejected by
    /// <see cref="CSS.Dom.CssDeclarationValidator.IsAcceptableDeclarationValue"/>
    /// are surfaced through
    /// <see cref="CSS.Dom.CssEngineDiagnostics.DeclarationRejected"/>
    /// (diagnostic #1b). The bridge rewrites the serialized <c>style</c> attribute
    /// from the survivors of this filter (see <c>PrepareCanonicalDocumentForRendering</c>),
    /// so a dropped inline declaration vanishes before the renderer's own style engine
    /// can report it — this is the only place such drops are observable. Set it only at
    /// inline-style <em>ingestion</em> sites that write <c>InlineStyle(element)</c> (so the drop
    /// reaches the rendered output); leave it off for query/bookkeeping re-parses and for
    /// stylesheet-rule / descriptor parsing (cascade drops the style engine already reports).
    /// </param>
    internal static Dictionary<string, string> ParseStyle(string styleValue, bool reportDrops = false)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var declarations = new CSS.CssParser().ParseDeclarations(styleValue);
        foreach (var declaration in declarations.Declarations)
        {
            var prop = declaration.Name;
            // Validate the importance-stripped value against the shared CSS.Dom
            // declaration table (the same closed-keyword error-recovery the cascade
            // uses), then re-attach the "!important" suffix the bridge-owned
            // declaration map carries as part of the string value.
            var rawValue = declaration.Value.Text;

            if (CssDeclarationValidator.IsAcceptableDeclarationValue(prop, rawValue))
            {
                var val = declaration.Important ? rawValue + " !important" : rawValue;
                result[prop] = val;
                // Map vendor-prefixed property to unprefixed equivalent (TODO-G9)
                var unprefixed = CssPropertyNames.StripVendorPrefix(prop);
                if (unprefixed != prop && !result.ContainsKey(unprefixed))
                    result[unprefixed] = val;
            }
            else if (reportDrops)
            {
                // Report the raw value (without any synthetic " !important" suffix) so
                // inline drops aggregate identically to the engine's stylesheet drops.
                CssEngineDiagnostics.DeclarationRejected?.Invoke(prop, rawValue);
            }
        }
        return result;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an acceptable declared value for
    /// <paramref name="property"/> per the shared <see cref="CSS.Dom.CssDeclarationValidator"/> —
    /// the same closed-keyword error-recovery the inline-style <em>attribute</em> path
    /// (<see cref="ParseStyle"/>) applies. A live <c>CSSStyleDeclaration</c> per-property setter
    /// (<c>el.style.color = …</c>, <c>setProperty(…)</c>, <c>cssFloat = …</c>) must <em>reject</em>
    /// an invalid value rather than store it, matching the attribute path (where
    /// <c>el.style = "color: bogus"</c> already drops the declaration) and CSSOM error handling.
    /// The value may carry a trailing <c>!important</c>, which is stripped before validation;
    /// unknown and custom (<c>--*</c>) properties are always accepted (the validator's default).
    /// </summary>
    internal static bool IsAcceptableInlineValue(string property, string value) =>
        CssDeclarationValidator.IsAcceptableDeclarationValue(property, CssPriority.Strip(value));

    [GeneratedRegex(@"<!DOCTYPE\s+(\w+)(?:\s+PUBLIC\s+""([^""]*)""(?:\s+""([^""]*)"")?)?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex DocTypePatternRegex();
}
