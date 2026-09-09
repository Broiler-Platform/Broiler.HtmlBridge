using System.Globalization;
using System.Text;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// neutral <c>internal static</c> <c>TryGetAttribute</c>/<c>CollectTextContent</c> helpers. Was the
/// bridge's <c>JsElementInterfacesCallback086Core</c>/<c>GetViewBox087Core</c>/
/// <c>GetNumberOfChars088Core</c>..<c>GetRotationOfChar093Core</c>/<c>SetCurrentTime095Core</c> (and the
/// private <c>CreateSvgLengthValue</c> helper, moved here since it had no other consumer).
/// </para>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type:
/// objects, accessors and methods come from the realm, which is handed in when the interfaces are
/// installed and arrives on the call frame for every accessor and method body afterwards. The three
/// SMIL no-ops used to be built by the bridge's <c>UndefinedFunction</c>/<c>ZeroFunction</c> factories,
/// which mint a plain <em>constructable</em> engine function; see the remarks on
/// <see cref="InstallSmilNoOps"/> for why they are asked of the realm as constructors here.
/// </para>
/// </summary>
internal static class SvgElementBinding
{
    /// <summary>
    /// Installs the SVG DOM interfaces on <paramref name="obj"/> when <paramref name="element"/> is an SVG
    /// element (SVG namespace or a recognised SVG tag). A no-op for non-SVG elements.
    /// </summary>
    /// <param name="realm">The realm the installed members and everything they build belong to.</param>
    /// <param name="obj">The element's JS wrapper.</param>
    /// <param name="element">The element the members read.</param>
    /// <param name="tag">The element's lower-cased tag name, which selects the interfaces.</param>
    public static void Install(IJsRealm realm, JsValue obj, DomElement element, string tag)
    {
        // -- Phase 6: SVG DOM interfaces --

        // SVG element properties — provide SVGAnimatedLength stubs for dimensional attributes
        if (!(element.NamespaceUri == "http://www.w3.org/2000/svg" ||
              tag == "svg" || tag == "rect" || tag == "circle" || tag == "ellipse" ||
              tag == "line" || tag == "polyline" || tag == "polygon" || tag == "path" ||
              tag == "text" || tag == "g" || tag == "use" || tag == "image" ||
              tag == "svg:svg" || tag == "svg:rect" || tag == "svg:text" || tag == "svg:g"))
            return;

        // For SVG dimensional attributes, provide SVGAnimatedLength objects with baseVal/animVal.
        // A null setter is how the read-only IDL attribute is spelled; the realm mints the accessor
        // function itself, names it "get width" and makes it non-constructable — the three things the
        // hand-built native accessor at this site was doing before.
        foreach (var dimAttr in new[] { "width", "height", "x", "y", "cx", "cy", "r", "rx", "ry" })
        {
            var attrName = dimAttr; // capture for closure
            realm.DefineAccessor(obj, attrName,
                (in call) => BuildAnimatedLength(call.Realm, attrName, element), null);
        }

        // SVG viewBox attribute — returns SVGAnimatedRect with baseVal {x,y,width,height}
        if (tag == "svg" || tag == "svg:svg")
        {
            realm.DefineAccessor(obj, "viewBox",
                (in call) => GetViewBox(call.Realm, element), null);
        }

        // SVGTextContentElement methods
        if (tag == "text" || tag == "svg:text" || tag == "tspan" || tag == "svg:tspan" ||
            tag == "textpath" || tag == "svg:textpath")
        {
            realm.DefineValue(obj, "getNumberOfChars",
                realm.NewMethod("getNumberOfChars", (in _) => GetNumberOfChars(element), 0));

            // getComputedTextLength() — returns estimated total advance width
            realm.DefineValue(obj, "getComputedTextLength",
                realm.NewMethod("getComputedTextLength", (in _) => GetComputedTextLength(element), 0));

            // getSubStringLength(charnum, nchars) — returns advance width of substring
            realm.DefineValue(obj, "getSubStringLength",
                realm.NewMethod("getSubStringLength", (in call) => GetSubStringLength(element, in call), 2));

            // getStartPositionOfChar(charnum) — returns SVGPoint {x, y}
            realm.DefineValue(obj, "getStartPositionOfChar",
                realm.NewMethod("getStartPositionOfChar", (in call) => GetStartPositionOfChar(element, in call), 1));

            // getEndPositionOfChar(charnum) — returns SVGPoint {x, y}
            realm.DefineValue(obj, "getEndPositionOfChar",
                realm.NewMethod("getEndPositionOfChar", (in call) => GetEndPositionOfChar(element, in call), 1));

            // getRotationOfChar(charnum) — returns rotation angle in degrees
            realm.DefineValue(obj, "getRotationOfChar",
                realm.NewMethod("getRotationOfChar", (in call) => GetRotationOfChar(element, in call), 1));
        }

        // SVGSVGElement methods (getCurrentTime, setCurrentTime)
        if (tag == "svg" || tag == "svg:svg")
        {
            // The timeline position is per wrapper and lives in this closure, exactly as it did before:
            // both methods capture the same local, so what setCurrentTime wrote getCurrentTime reads.
            double currentTime = 0;

            realm.DefineValue(obj, "getCurrentTime",
                realm.NewMethod("getCurrentTime", (in _) => JsValue.Number(currentTime), 0));

            realm.DefineValue(obj, "setCurrentTime",
                realm.NewMethod("setCurrentTime", (in call) => SetCurrentTime(ref currentTime, in call), 1));
        }

        // SMIL animation element methods (beginElement, endElement, getStartTime)
        if (tag == "set" || tag == "svg:set" ||
            tag == "animate" || tag == "svg:animate" ||
            tag == "animatetransform" || tag == "svg:animatetransform" ||
            tag == "animatemotion" || tag == "svg:animatemotion")
        {
            InstallSmilNoOps(realm, obj);
        }
    }

    /// <summary>
    /// The three SMIL animation-element no-ops: <c>beginElement</c>, <c>endElement</c> and
    /// <c>getStartTime</c>, which report nothing because Broiler runs no SMIL timeline.
    /// </summary>
    /// <remarks>
    /// All three are <em>constructable</em>, and only because they always have been: they were built by
    /// the bridge's <c>UndefinedFunction</c>/<c>ZeroFunction</c> helpers, which mint a plain engine
    /// function — one that carries a <c>prototype</c> object and so passes the engine's constructor test
    /// — rather than the non-constructable shape WebIDL gives an operation. Under JSEAL that distinction
    /// is which factory is called, so preserving the behaviour means asking for a constructor here. A
    /// browser answers <c>undefined</c> for <c>el.beginElement.prototype</c> and throws on
    /// <c>new el.beginElement()</c>; correcting that is a behaviour change that belongs in its own commit
    /// alongside the helpers' other callers, as <see cref="ScreenOrientationBinding"/> records for
    /// <c>screen.orientation.unlock</c>.
    /// </remarks>
    private static void InstallSmilNoOps(IJsRealm realm, JsValue obj)
    {
        realm.DefineValue(obj, "beginElement",
            realm.NewConstructor("beginElement", static (in _) => JsValue.Undefined, 0));

        realm.DefineValue(obj, "endElement",
            realm.NewConstructor("endElement", static (in _) => JsValue.Undefined, 0));

        realm.DefineValue(obj, "getStartTime",
            realm.NewConstructor("getStartTime", static (in _) => JsValue.Number(0), 0));
    }

    // SVGAnimatedLength stub for a dimensional presentation attribute — baseVal/animVal each an SVGLength.
    private static JsValue BuildAnimatedLength(IJsRealm realm, string attrName, DomElement element)
    {
        var animLength = realm.NewObject();
        var valueStr = DomBridge.TryGetAttribute(element, attrName, out var v) ? v : "0";
        double.TryParse(valueStr, NumberStyles.Any, CultureInfo.InvariantCulture, out var numVal);
        var baseVal = CreateSvgLengthValue(realm, numVal);
        var animVal = CreateSvgLengthValue(realm, numVal);
        realm.DefineValue(animLength, "baseVal", baseVal);
        realm.DefineValue(animLength, "animVal", animVal);
        return animLength;
    }

    // SVGAnimatedRect for the viewBox attribute — baseVal/animVal share one parsed {x,y,width,height}.
    private static JsValue GetViewBox(IJsRealm realm, DomElement element)
    {
        var animRect = realm.NewObject();
        var baseRect = realm.NewObject();
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

        realm.DefineValue(baseRect, "x", JsValue.Number(vbX));
        realm.DefineValue(baseRect, "y", JsValue.Number(vbY));
        realm.DefineValue(baseRect, "width", JsValue.Number(vbW));
        realm.DefineValue(baseRect, "height", JsValue.Number(vbH));
        realm.DefineValue(animRect, "baseVal", baseRect);
        realm.DefineValue(animRect, "animVal", baseRect);
        return animRect;
    }

    private static JsValue GetNumberOfChars(DomElement element)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        return JsValue.Number(sb.Length);
    }

    private static JsValue GetComputedTextLength(DomElement element)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        // Stub: estimate using font-size * character count * 0.6 average advance ratio
        var fontSize = ReadFontSize(element);
        return JsValue.Number(sb.Length * fontSize * 0.6);
    }

    private static JsValue GetSubStringLength(DomElement element, in JsCall call)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        // The realm's ToNumber, not the handle's: the engine's DoubleValue on an argument *was* the
        // ECMAScript coercion, so `getSubStringLength("1", "2")` has always counted from character 1,
        // and an argument object's valueOf has always been allowed to run here.
        var charnum = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        var nchars = call.Length > 1 ? (int)call.Realm.ToNumber(call[1]) : 0;
        if (charnum < 0 || charnum >= sb.Length)
            throw call.Realm.Error(JsErrorKind.Error, "INDEX_SIZE_ERR");
        if (nchars == 0)
            return JsValue.Number(0);
        var fontSize = ReadFontSize(element);
        return JsValue.Number(nchars * fontSize * 0.6);
    }

    private static JsValue GetStartPositionOfChar(DomElement element, in JsCall call)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        var charnum = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        if (charnum < 0 || charnum >= sb.Length)
            throw call.Realm.Error(JsErrorKind.Error, "INDEX_SIZE_ERR");
        var fontSize = ReadFontSize(element);

        var pt = call.Realm.NewObject();
        call.Realm.DefineValue(pt, "x", JsValue.Number(charnum * fontSize * 0.6));
        call.Realm.DefineValue(pt, "y", JsValue.Number(fontSize));
        return pt;
    }

    private static JsValue GetEndPositionOfChar(DomElement element, in JsCall call)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        var charnum = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        if (charnum < 0 || charnum >= sb.Length)
            throw call.Realm.Error(JsErrorKind.Error, "INDEX_SIZE_ERR");
        var fontSize = ReadFontSize(element);

        var pt = call.Realm.NewObject();
        call.Realm.DefineValue(pt, "x", JsValue.Number((charnum + 1) * fontSize * 0.6));
        call.Realm.DefineValue(pt, "y", JsValue.Number(fontSize));
        return pt;
    }

    private static JsValue GetRotationOfChar(DomElement element, in JsCall call)
    {
        var sb = new StringBuilder();
        DomBridge.CollectTextContent(element, sb);
        var charnum = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        if (charnum < 0 || charnum >= sb.Length)
            throw call.Realm.Error(JsErrorKind.Error, "INDEX_SIZE_ERR");
        // Default rotation is 0 degrees (horizontal text)
        return JsValue.Number(0);
    }

    private static JsValue SetCurrentTime(ref double currentTime, in JsCall call)
    {
        if (call.Length > 0)
            currentTime = call.Realm.ToNumber(call[0]);
        return JsValue.Undefined;
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
    private static JsValue CreateSvgLengthValue(IJsRealm realm, double numericValue)
    {
        var svgLength = realm.NewObject();
        realm.DefineValue(svgLength, "value", JsValue.Number(numericValue));
        realm.DefineValue(svgLength, "valueInSpecifiedUnits", JsValue.Number(numericValue));
        realm.DefineValue(svgLength, "unitType", JsValue.Number(1));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_UNKNOWN", JsValue.Number(0));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_NUMBER", JsValue.Number(1));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_PERCENTAGE", JsValue.Number(2));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_EMS", JsValue.Number(3));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_EXS", JsValue.Number(4));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_PX", JsValue.Number(5));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_CM", JsValue.Number(6));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_MM", JsValue.Number(7));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_IN", JsValue.Number(8));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_PT", JsValue.Number(9));
        realm.DefineValue(svgLength, "SVG_LENGTHTYPE_PC", JsValue.Number(10));
        return svgLength;
    }
}
