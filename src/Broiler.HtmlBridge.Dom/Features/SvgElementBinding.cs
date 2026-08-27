using System.Globalization;
using System.Text;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// SVG DOM element interfaces, co-located as an HtmlBridge feature module (Phase 3): the
/// <c>SVGAnimatedLength</c> stubs for the dimensional presentation attributes
/// (<c>width</c>/<c>height</c>/<c>x</c>/<c>y</c>/<c>cx</c>/<c>cy</c>/<c>r</c>/<c>rx</c>/<c>ry</c>), the
/// <c>SVGSVGElement.viewBox</c> <c>SVGAnimatedRect</c>, the <c>SVGTextContentElement</c> text-metric
/// methods (<c>getNumberOfChars</c>/<c>getComputedTextLength</c>/<c>getSubStringLength</c>/
/// <c>getStartPositionOfChar</c>/<c>getEndPositionOfChar</c>/<c>getRotationOfChar</c>), the
/// <c>SVGSVGElement</c> animation timeline (<c>getCurrentTime</c>/<c>setCurrentTime</c>) and the SMIL
/// animation-element no-ops (<c>beginElement</c>/<c>endElement</c>/<c>getStartTime</c>).
/// <para>
/// Every accessor here is an attribute/font-size estimation stub — none reads layout geometry — so the
/// module is a pure <c>internal static</c> class with <b>no host contract</b> (like <c>ClassListBinding</c>
/// P3.6 and <c>WebStorageBinding</c> P3.48). It reads content attributes and text through the bridge's
/// neutral <c>internal static</c> <c>TryGetAttribute</c>/<c>CollectTextContent</c> helpers, and builds the
/// no-op SMIL functions with the bridge's <c>internal static</c> <c>UndefinedFunction</c>/<c>ZeroFunction</c>
/// factories. Was the bridge's <c>JsElementInterfacesCallback086Core</c>/<c>GetViewBox087Core</c>/
/// <c>GetNumberOfChars088Core</c>..<c>GetRotationOfChar093Core</c>/<c>SetCurrentTime095Core</c> (and the
/// private <c>CreateSvgLengthValue</c> helper, moved here since it had no other consumer).
/// </para>
/// </summary>
internal static class SvgElementBinding
{
    /// <summary>
    /// Installs the SVG DOM interfaces on <paramref name="obj"/> when <paramref name="element"/> is an SVG
    /// element (SVG namespace or a recognised SVG tag). A no-op for non-SVG elements.
    /// </summary>
    public static void Install(JSObject obj, DomElement element, string tag)
    {
        // -- Phase 6: SVG DOM interfaces --

        // SVG element properties — provide SVGAnimatedLength stubs for dimensional attributes
        if (!(element.NamespaceUri == "http://www.w3.org/2000/svg" ||
              tag == "svg" || tag == "rect" || tag == "circle" || tag == "ellipse" ||
              tag == "line" || tag == "polyline" || tag == "polygon" || tag == "path" ||
              tag == "text" || tag == "g" || tag == "use" || tag == "image" ||
              tag == "svg:svg" || tag == "svg:rect" || tag == "svg:text" || tag == "svg:g"))
            return;

        // For SVG dimensional attributes, provide SVGAnimatedLength objects with baseVal/animVal
        foreach (var dimAttr in new[] { "width", "height", "x", "y", "cx", "cy", "r", "rx", "ry" })
        {
            var attrName = dimAttr; // capture for closure
            obj.FastAddProperty(attrName,
                new DomFunction((in _) => BuildAnimatedLength(attrName, element), $"get {attrName}"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);
        }

        // SVG viewBox attribute — returns SVGAnimatedRect with baseVal {x,y,width,height}
        if (tag == "svg" || tag == "svg:svg")
        {
            obj.FastAddProperty("viewBox",
                new DomFunction((in _) => GetViewBox(element), "get viewBox"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);
        }

        // SVGTextContentElement methods
        if (tag == "text" || tag == "svg:text" || tag == "tspan" || tag == "svg:tspan" ||
            tag == "textpath" || tag == "svg:textpath")
        {
            obj.FastAddValue("getNumberOfChars",
                new DomFunction((in _) => GetNumberOfChars(element), "getNumberOfChars", 0),
                JSPropertyAttributes.EnumerableConfigurableValue);

            // getComputedTextLength() — returns estimated total advance width
            obj.FastAddValue("getComputedTextLength",
                new DomFunction((in _) => GetComputedTextLength(element), "getComputedTextLength", 0),
                JSPropertyAttributes.EnumerableConfigurableValue);

            // getSubStringLength(charnum, nchars) — returns advance width of substring
            obj.FastAddValue("getSubStringLength",
                new DomFunction((in a) => GetSubStringLength(element, in a), "getSubStringLength", 2),
                JSPropertyAttributes.EnumerableConfigurableValue);

            // getStartPositionOfChar(charnum) — returns SVGPoint {x, y}
            obj.FastAddValue("getStartPositionOfChar",
                new DomFunction((in a) => GetStartPositionOfChar(element, in a), "getStartPositionOfChar", 1),
                JSPropertyAttributes.EnumerableConfigurableValue);

            // getEndPositionOfChar(charnum) — returns SVGPoint {x, y}
            obj.FastAddValue("getEndPositionOfChar",
                new DomFunction((in a) => GetEndPositionOfChar(element, in a), "getEndPositionOfChar", 1),
                JSPropertyAttributes.EnumerableConfigurableValue);

            // getRotationOfChar(charnum) — returns rotation angle in degrees
            obj.FastAddValue("getRotationOfChar",
                new DomFunction((in a) => GetRotationOfChar(element, in a), "getRotationOfChar", 1),
                JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // SVGSVGElement methods (getCurrentTime, setCurrentTime)
        if (tag == "svg" || tag == "svg:svg")
        {
            double currentTime = 0;

            obj.FastAddValue("getCurrentTime",
                new DomFunction((in _) => new JSNumber(currentTime), "getCurrentTime", 0),
                JSPropertyAttributes.EnumerableConfigurableValue);

            obj.FastAddValue("setCurrentTime",
                new DomFunction((in a) => SetCurrentTime(ref currentTime, in a), "setCurrentTime", 1),
                JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // SMIL animation element methods (beginElement, endElement, getStartTime)
        if (tag == "set" || tag == "svg:set" ||
            tag == "animate" || tag == "svg:animate" ||
            tag == "animatetransform" || tag == "svg:animatetransform" ||
            tag == "animatemotion" || tag == "svg:animatemotion")
        {
            obj.FastAddValue("beginElement",
                DomBridge.UndefinedFunction("beginElement", 0),
                JSPropertyAttributes.EnumerableConfigurableValue);

            obj.FastAddValue("endElement",
                DomBridge.UndefinedFunction("endElement", 0),
                JSPropertyAttributes.EnumerableConfigurableValue);

            obj.FastAddValue("getStartTime",
                DomBridge.ZeroFunction("getStartTime", 0),
                JSPropertyAttributes.EnumerableConfigurableValue);
        }
    }

    // SVGAnimatedLength stub for a dimensional presentation attribute — baseVal/animVal each an SVGLength.
    private static JSValue BuildAnimatedLength(string attrName, DomElement element)
    {
        var animLength = new JSObject();
        var valueStr = DomBridge.TryGetAttribute(element, attrName, out var v) ? v : "0";
        double.TryParse(valueStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var numVal);
        var baseVal = CreateSvgLengthValue(numVal);
        var animVal = CreateSvgLengthValue(numVal);
        animLength.FastAddValue("baseVal", baseVal, JSPropertyAttributes.EnumerableConfigurableValue);
        animLength.FastAddValue("animVal", animVal, JSPropertyAttributes.EnumerableConfigurableValue);
        return animLength;
    }

    // SVGAnimatedRect for the viewBox attribute — baseVal/animVal share one parsed {x,y,width,height}.
    private static JSValue GetViewBox(DomElement element)
    {
        var animRect = new JSObject();
        var baseRect = new JSObject();
        double vbX = 0, vbY = 0, vbW = 0, vbH = 0;
        if (DomBridge.TryGetAttribute(element, "viewBox", out var vb) && !string.IsNullOrWhiteSpace(vb))
        {
            var parts = vb.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4)
            {
                double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out vbX);
                double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out vbY);
                double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out vbW);
                double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out vbH);
            }
        }

        baseRect.FastAddValue("x", new JSNumber(vbX), JSPropertyAttributes.EnumerableConfigurableValue);
        baseRect.FastAddValue("y", new JSNumber(vbY), JSPropertyAttributes.EnumerableConfigurableValue);
        baseRect.FastAddValue("width", new JSNumber(vbW), JSPropertyAttributes.EnumerableConfigurableValue);
        baseRect.FastAddValue("height", new JSNumber(vbH), JSPropertyAttributes.EnumerableConfigurableValue);
        animRect.FastAddValue("baseVal", baseRect, JSPropertyAttributes.EnumerableConfigurableValue);
        animRect.FastAddValue("animVal", baseRect, JSPropertyAttributes.EnumerableConfigurableValue);
        return animRect;
    }

    private static JSValue GetNumberOfChars(DomElement element)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        return new JSNumber(sb.Length);
    }

    private static JSValue GetComputedTextLength(DomElement element)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        // Stub: estimate using font-size * character count * 0.6 average advance ratio
        var fontSize = ReadFontSize(element);
        return new JSNumber(sb.Length * fontSize * 0.6);
    }

    private static JSValue GetSubStringLength(DomElement element, in Arguments a)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        var charnum = a.Length > 0 ? (int)a[0].DoubleValue : 0;
        var nchars = a.Length > 1 ? (int)a[1].DoubleValue : 0;
        if (charnum < 0 || charnum >= sb.Length)
            throw new JSException("INDEX_SIZE_ERR");
        if (nchars == 0)
            return new JSNumber(0);
        var fontSize = ReadFontSize(element);
        return new JSNumber(nchars * fontSize * 0.6);
    }

    private static JSValue GetStartPositionOfChar(DomElement element, in Arguments a)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        var charnum = a.Length > 0 ? (int)a[0].DoubleValue : 0;
        if (charnum < 0 || charnum >= sb.Length)
            throw new JSException("INDEX_SIZE_ERR");
        var fontSize = ReadFontSize(element);

        var pt = new JSObject();
        pt.FastAddValue("x", new JSNumber(charnum * fontSize * 0.6), JSPropertyAttributes.EnumerableConfigurableValue);
        pt.FastAddValue("y", new JSNumber(fontSize), JSPropertyAttributes.EnumerableConfigurableValue);
        return pt;
    }

    private static JSValue GetEndPositionOfChar(DomElement element, in Arguments a)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        var charnum = a.Length > 0 ? (int)a[0].DoubleValue : 0;
        if (charnum < 0 || charnum >= sb.Length)
            throw new JSException("INDEX_SIZE_ERR");
        var fontSize = ReadFontSize(element);

        var pt = new JSObject();
        pt.FastAddValue("x", new JSNumber((charnum + 1) * fontSize * 0.6), JSPropertyAttributes.EnumerableConfigurableValue);
        pt.FastAddValue("y", new JSNumber(fontSize), JSPropertyAttributes.EnumerableConfigurableValue);
        return pt;
    }

    private static JSValue GetRotationOfChar(DomElement element, in Arguments a)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        var charnum = a.Length > 0 ? (int)a[0].DoubleValue : 0;
        if (charnum < 0 || charnum >= sb.Length)
            throw new JSException("INDEX_SIZE_ERR");
        // Default rotation is 0 degrees (horizontal text)
        return new JSNumber(0);
    }

    private static JSValue SetCurrentTime(ref double currentTime, in Arguments a)
    {
        if (a.Length > 0)
            currentTime = a[0].DoubleValue;
        return JSUndefined.Value;
    }

    // Reads the element's font-size presentation attribute (px/pt suffix tolerated), defaulting to 16.
    private static double ReadFontSize(DomElement element)
    {
        double fontSize = 16;
        if (DomBridge.TryGetAttribute(element, "font-size", out var fs))
        {
            var fsClean = fs.Replace("px", "").Replace("pt", "").Trim();
            double.TryParse(fsClean, NumberStyles.Any, CultureInfo.InvariantCulture, out fontSize);
        }

        return fontSize;
    }

    // Builds the SVGLength value object (value/valueInSpecifiedUnits/unitType + the SVG_LENGTHTYPE_* constants).
    private static JSObject CreateSvgLengthValue(double numericValue)
    {
        var svgLength = new JSObject();
        svgLength.FastAddValue("value", new JSNumber(numericValue), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("valueInSpecifiedUnits", new JSNumber(numericValue), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("unitType", new JSNumber(1), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_UNKNOWN", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_NUMBER", new JSNumber(1), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_PERCENTAGE", new JSNumber(2), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_EMS", new JSNumber(3), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_EXS", new JSNumber(4), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_PX", new JSNumber(5), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_CM", new JSNumber(6), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_MM", new JSNumber(7), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_IN", new JSNumber(8), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_PT", new JSNumber(9), JSPropertyAttributes.EnumerableConfigurableValue);
        svgLength.FastAddValue("SVG_LENGTHTYPE_PC", new JSNumber(10), JSPropertyAttributes.EnumerableConfigurableValue);
        return svgLength;
    }
}
