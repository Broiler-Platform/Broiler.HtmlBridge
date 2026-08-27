using System.Text.RegularExpressions;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>DomBridge.Serialization.cs</c> (Phase 3 ratchet, 2026-07-17)
/// to keep it under the 750-line guard: the cohesive SVG zoom-serialization attribute-scaling
/// cluster. When a subtree carries a used <c>zoom</c>, serialization bakes it into the SVG
/// presentation/geometry attributes (<c>fill</c>/<c>stroke</c>, <c>width</c>/<c>height</c>,
/// <c>points</c>, path <c>d</c>, …) by scaling each length token — resolving font-relative and
/// root-font-relative units against the element's specified font size and the owning element's
/// used zoom. Pure partial-class relocation — no signature, accessibility, or logic change.
/// Entered from <c>ApplyZoomSerializationStyles</c> via <c>ApplyZoomSerializationSvgAttributes</c>.
/// </summary>
public sealed partial class DomBridge
{
    private void ApplyZoomSerializationSvgAttributes(DomElement element, double usedZoom)
    {
        var tag = element.TagName.ToLowerInvariant();
        var props = GetComputedProps(element);

        ApplySvgPresentationAttribute(element, props, "fill", cascadeWins: true);
        ApplySvgPresentationAttribute(element, props, "stroke", cascadeWins: true);
        ApplySvgPresentationAttribute(element, props, "stroke-width", preferInlineStyle: true);

        // CSS Transforms 1 §6/§8. Both are presentation attributes in SVG 2 and both are read off
        // the serialized markup by SvgRenderer, which sees no stylesheet of its own — so a
        // `rect { transform-box: fill-box }` rule reached nothing at all, and the element's
        // `transform` kept turning about the viewport origin instead of about its own box. That is
        // the whole of what the 45 css-transforms/transform-origin/svg-origin-* tests measure;
        // every one of them declares transform-box in a <style> block rather than as an attribute.
        // Neither inherits, so the cascaded value is this element's own and may overwrite the
        // attribute — the same reasoning `fill` and `stroke` are given above.
        // `transform` itself is the third of them, and it was the one left out — so a
        // `#target { transform: rotate(90deg) }` rule reached nothing and the element rendered
        // untransformed, which is the whole of what the css-transforms/transform-box `svgbox-*`
        // family measures: each declares its transform in a <style> block. `transform-box` and
        // `transform-origin` were already projected, and on their own they can only move a
        // transform that arrived by attribute.
        //
        // A value that parses no function at all is skipped rather than written, and that is not a
        // detail — it is the rule CSS Transforms 1 §3 states. This bridge's cascade does not model
        // SVG presentation attributes as declarations, so an element carrying
        // `transform="translate(50)"` and no rule computes `none`, and writing that back would
        // erase the attribute the renderer was going to read. The same guard is what makes an
        // *invalid* declaration fall back to the attribute instead of destroying it:
        // `transform: scale(invalid)` must leave `transform="rotate(90)"` standing, which is
        // exactly what the nine svg-{document,external,inline}-styles-005/006/013 tests assert.
        ApplySvgPresentationAttribute(
            element, props, "transform", cascadeWins: true,
            accept: static value => Layout.IR.SvgTransform.TryParse(value, out _));
        ApplySvgPresentationAttribute(element, props, "transform-box", cascadeWins: true);
        ApplySvgPresentationAttribute(element, props, "transform-origin", cascadeWins: true);

        if (tag is "text" or "textpath")
        {
            ApplySvgPresentationAttribute(element, props, "font-size", preferInlineStyle: true);
            ApplySvgPresentationAttribute(element, props, "font-family");
        }

        switch (tag)
        {
            case "svg":
                ScaleSvgLengthAttribute(element, "width", usedZoom);
                ScaleSvgLengthAttribute(element, "height", usedZoom);
                break;
            case "rect":
                ScaleSvgLengthAttribute(element, "x", usedZoom);
                ScaleSvgLengthAttribute(element, "y", usedZoom);
                ScaleSvgLengthAttribute(element, "width", usedZoom);
                ScaleSvgLengthAttribute(element, "height", usedZoom);
                break;
            case "line":
                ScaleSvgLengthAttribute(element, "x1", usedZoom);
                ScaleSvgLengthAttribute(element, "x2", usedZoom);
                ScaleSvgLengthAttribute(element, "y1", usedZoom);
                ScaleSvgLengthAttribute(element, "y2", usedZoom);
                break;
            case "text":
                ScaleSvgLengthAttribute(element, "x", usedZoom);
                ScaleSvgLengthAttribute(element, "y", usedZoom);
                break;
            case "polygon":
            case "polyline":
                ScaleSvgPointListAttribute(element, "points", usedZoom);
                break;
            case "path":
                ScaleSvgPathDataAttribute(element, "d", usedZoom);
                break;
        }
    }

    /// <param name="cascadeWins">
    /// The cascaded value overwrites an existing presentation attribute rather than deferring to
    /// it. SVG 1.1 §6.4 ranks a presentation attribute as an author-origin rule of specificity 0
    /// inserted at the *start* of the author sheet, so any author rule outranks it — the deferral
    /// has the priority backwards, and `:lang(en) { fill: green }` lost to a `fill="none"`
    /// attribute (WPT conformance-checkers/html-svg/styling-css-05-b-isvalid).
    /// <para>
    /// Set only for <c>fill</c> and <c>stroke</c>. The font properties are inherited, so their
    /// cascaded value on an SVG element is whatever the enclosing document sets: overwriting
    /// there would clobber a <c>font-family="SVGFreeSansASCII"</c> attribute with the body font,
    /// which is a regression rather than a cascade.
    /// </para>
    /// </param>
    private void ApplySvgPresentationAttribute(
        DomElement element, Dictionary<string, string> props, string propertyName,
        bool preferInlineStyle = false, bool cascadeWins = false, Func<string, bool>? accept = null)
    {
        if (!cascadeWins && HasAttr(element, propertyName))
            return;

        string? value = null;
        if (preferInlineStyle && BakedInlineStyle(element).TryGetValue(propertyName, out var inlineValue) && !string.IsNullOrWhiteSpace(inlineValue))
            value = inlineValue;
        else if (props.TryGetValue(propertyName, out var propValue) && !string.IsNullOrWhiteSpace(propValue))
            value = propValue;
        else if (preferInlineStyle && props.TryGetValue(propertyName, out var fallbackProp) && !string.IsNullOrWhiteSpace(fallbackProp))
            value = fallbackProp;

        if (string.IsNullOrWhiteSpace(value))
            return;

        // A value the caller will not accept carries no author intent this attribute should take,
        // and writing it would overwrite one that does. See the `transform` call site for why that
        // is not hypothetical.
        if (accept is not null && !accept(value.Trim()))
            return;

        SetAttr(element, propertyName, value.Trim());
    }

    private void ScaleSvgLengthAttribute(DomElement element, string attributeName, double usedZoom)
    {
        if (!TryGetAttribute(element, attributeName, out var value) ||
            !TryScaleSvgLengthToken(element, value, usedZoom, out var scaled))
        {
            return;
        }

        SetAttr(element, attributeName, scaled);
    }

    private void ScaleSvgPointListAttribute(DomElement element, string attributeName, double usedZoom)
    {
        if (!TryGetAttribute(element, attributeName, out var value) || string.IsNullOrWhiteSpace(value))
            return;

        SetAttr(element, attributeName, ScaleSvgPointRegex().Replace(value, match => ScaleSvgNumericMatch(match, usedZoom)));
    }

    private void ScaleSvgPathDataAttribute(DomElement element, string attributeName, double usedZoom)
    {
        if (!TryGetAttribute(element, attributeName, out var value) || string.IsNullOrWhiteSpace(value))
            return;

        SetAttr(element, attributeName, ScaleSvgPathRegex().Replace(value, match => ScaleSvgNumericMatch(match, usedZoom)));
    }

    private static string ScaleSvgNumericMatch(Match match, double factor)
    {
        if (!double.TryParse(match.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return match.Value;
        }

        return (number * factor).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    private bool TryScaleSvgLengthToken(DomElement element, string value, double usedZoom, out string scaled)
    {
        scaled = string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.EndsWith('%'))
            return false;

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var unitlessNumber))
        {
            scaled = (unitlessNumber * usedZoom).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            return true;
        }

        foreach (var unit in SvgZoomScaledUnits)
        {
            if (!trimmed.EndsWith(unit, StringComparison.OrdinalIgnoreCase))
                continue;

            var numericPart = trimmed[..^unit.Length];
            if (!double.TryParse(numericPart, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            if (TryResolveSvgFontRelativeUnitPixels(element, unit, out var unitPixels))
            {
                scaled = (number * unitPixels * usedZoom)
                    .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            var factor = ResolveSvgLengthZoomFactor(element, unit, usedZoom);
            if (Math.Abs(factor - 1.0) < ZoomSerializationEpsilon)
                return false;

            scaled = $"{(number * factor).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}{unit}";
            return true;
        }

        return false;
    }

    private bool TryResolveSvgFontRelativeUnitPixels(DomElement element, string unit, out double pixels)
    {
        pixels = 0;
        if (SvgRootFontRelativeUnits.Contains(unit))
        {
            pixels = ResolveOriginalRootSpecifiedFontSizePx() * GetSvgFontRelativeUnitRatio(unit);
            return pixels > 0;
        }

        if (!SvgFontRelativeUnits.Contains(unit))
            return false;

        pixels = ResolveOriginalNearestSpecifiedFontSizePx(element) * GetSvgFontRelativeUnitRatio(unit);
        return pixels > 0;
    }

    private double ResolveOriginalNearestSpecifiedFontSizePx(DomElement element)
    {
        for (DomElement? current = element; current != null; current = ParentEl(current))
        {
            if (TryGetSpecifiedFontSizePx(current, out var fontSize))
                return fontSize;
        }

        return ResolveOriginalRootSpecifiedFontSizePx();
    }

    private double ResolveOriginalRootSpecifiedFontSizePx() =>
        TryGetSpecifiedFontSizePx(DocumentElement, out var fontSize) ? fontSize : 16;

    private bool TryGetSpecifiedFontSizePx(DomElement element, out double fontSize)
    {
        fontSize = 0;
        var specified = BuildSpecifiedStyleMap(element);
        if (TryParsePx(specified.GetValueOrDefault("font-size")) is double px)
        {
            fontSize = px;
            return true;
        }

        if (!specified.TryGetValue("font", out var fontShorthand) || string.IsNullOrWhiteSpace(fontShorthand))
            return false;

        var sizeMatch = FontShortHandRegex().Match(fontShorthand);
        if (!sizeMatch.Success ||
            !double.TryParse(sizeMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out fontSize))
        {
            return false;
        }

        return true;
    }

    private static double GetSvgFontRelativeUnitRatio(string unit) => unit.ToLowerInvariant() switch
    {
        // Broiler's SVG length resolution currently uses the same deterministic
        // Ahem-like 0.8em approximation that the existing font-relative zoom
        // coverage already assumes for ex/cap units.
        "ex" or "rex" or "cap" or "rcap" => 0.8,
        _ => 1.0
    };

    private double ResolveSvgLengthZoomFactor(DomElement element, string unit, double usedZoom)
    {
        if (SvgAbsoluteOrViewportUnits.Contains(unit))
            return usedZoom;

        if (SvgRootFontRelativeUnits.Contains(unit))
            return usedZoom / GetRootFontSizeOwnerZoom();

        if (SvgFontRelativeUnits.Contains(unit))
            return usedZoom / GetNearestExplicitFontSizeOwnerZoom(element);

        return usedZoom;
    }

    private double GetNearestExplicitFontSizeOwnerZoom(DomElement element)
    {
        for (DomElement? current = element; current != null; current = ParentEl(current))
        {
            var props = GetComputedProps(current);
            if (props.TryGetValue("font-size", out var fontSize) && !string.IsNullOrWhiteSpace(fontSize))
                return GetUsedZoomForElement(current);
        }

        return 1.0;
    }

    private double GetRootFontSizeOwnerZoom()
    {
        var props = GetComputedProps(DocumentElement);
        if (props.TryGetValue("font-size", out var fontSize) && !string.IsNullOrWhiteSpace(fontSize))
            return GetUsedZoomForElement(DocumentElement);

        return 1.0;
    }

    private static readonly string[] SvgZoomScaledUnits =
    [
        "rcap", "rch", "ric", "rex", "rlh", "rem",
        "vmin", "vmax",
        "cap",
        "em", "ex", "ch", "ic", "lh",
        "vw", "vh",
        "px", "pt", "pc", "cm", "mm", "in", "q"
    ];

    private static readonly HashSet<string> SvgAbsoluteOrViewportUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "vw", "vh", "vmin", "vmax",
        "px", "pt", "pc", "cm", "mm", "in", "q"
    };

    private static readonly HashSet<string> SvgFontRelativeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "em", "ex", "cap", "ch", "ic", "lh"
    };

    private static readonly HashSet<string> SvgRootFontRelativeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "rem", "rex", "rcap", "rch", "ric", "rlh"
    };

    [GeneratedRegex(@"-?\d*\.?\d+(?:[eE][+-]?\d+)?")]
    private static partial System.Text.RegularExpressions.Regex ScaleSvgPointRegex();

    [GeneratedRegex(@"-?\d*\.?\d+(?:[eE][+-]?\d+)?")]
    private static partial System.Text.RegularExpressions.Regex ScaleSvgPathRegex();

    [GeneratedRegex(@"(?<![\w.-])(-?\d*\.?\d+)px(?:\s*/|(?=\s|$))", RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FontShortHandRegex();
}
