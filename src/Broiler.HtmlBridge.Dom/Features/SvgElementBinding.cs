using System.Globalization;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// SVG DOM element interfaces, co-located as an HtmlBridge feature module: the
/// <c>SVGAnimatedLength</c> stubs for the dimensional presentation attributes
/// (<c>width</c>/<c>height</c>/<c>x</c>/<c>y</c>/<c>cx</c>/<c>cy</c>/<c>r</c>/<c>rx</c>/<c>ry</c>), the
/// <c>SVGSVGElement.viewBox</c> <c>SVGAnimatedRect</c>, the <c>SVGTextContentElement</c> text-metric
/// methods (<c>getNumberOfChars</c>/<c>getComputedTextLength</c>/<c>getSubStringLength</c>/
/// <c>getStartPositionOfChar</c>/<c>getEndPositionOfChar</c>/<c>getRotationOfChar</c>), the
/// <c>SVGSVGElement</c> animation timeline (<c>getCurrentTime</c>/<c>setCurrentTime</c>) and the SMIL
/// animation-element no-ops (<c>beginElement</c>/<c>endElement</c>/<c>getStartTime</c>).
/// <para>
/// Every accessor here is an attribute/font-size estimation stub — none reads layout geometry — so the
/// module is a pure <c>internal static</c> class with <b>no host contract</b> (like
/// <c>ClassListBinding</c> and <c>WebStorageBinding</c>). It reads content attributes through the
/// bridge's neutral <c>internal static</c> <c>TryGetAttribute</c> helper and text through the
/// canonical <see cref="DomNode.TextContent"/>.
/// </para>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type:
/// objects, accessors and methods come from the realm, which is handed in when the interfaces are
/// installed and arrives on the call frame for every accessor and method body afterwards. The three
/// SMIL no-ops are asked of the realm as <em>constructors</em> rather than methods; see the remarks
/// on <see cref="InstallSmilNoOps"/> for why.
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
        // -- SVG DOM interfaces --

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
            realm.DefineMethod(obj, "getNumberOfChars", 0, (in _) => GetNumberOfChars(element));

            // getComputedTextLength() — returns estimated total advance width
            realm.DefineMethod(obj, "getComputedTextLength", 0, (in _) => GetComputedTextLength(element));

            // getSubStringLength(charnum, nchars) — returns advance width of substring
            realm.DefineMethod(obj, "getSubStringLength", 2, (in call) => GetSubStringLength(element, in call));

            // getStartPositionOfChar(charnum) — returns SVGPoint {x, y}
            realm.DefineMethod(obj, "getStartPositionOfChar", 1,
                (in call) => GetStartPositionOfChar(element, in call));

            // getEndPositionOfChar(charnum) — returns SVGPoint {x, y}
            realm.DefineMethod(obj, "getEndPositionOfChar", 1, (in call) => GetEndPositionOfChar(element, in call));

            // getRotationOfChar(charnum) — returns rotation angle in degrees
            realm.DefineMethod(obj, "getRotationOfChar", 1, (in call) => GetRotationOfChar(element, in call));
        }

        // SVGSVGElement methods (getCurrentTime, setCurrentTime)
        if (tag == "svg" || tag == "svg:svg")
        {
            // The timeline position is per wrapper and lives in this closure, exactly as it did before:
            // both methods capture the same local, so what setCurrentTime wrote getCurrentTime reads.
            double currentTime = 0;

            realm.DefineMethod(obj, "getCurrentTime", 0, (in _) => JsValue.Number(currentTime));

            realm.DefineMethod(obj, "setCurrentTime", 1, (in call) => SetCurrentTime(ref currentTime, in call));
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
    /// All three are <em>constructable</em>, deliberately and only for compatibility with the shape
    /// the bridge published for a long time: a plain function — one that carries a <c>prototype</c>
    /// object and so passes the engine's constructor test — rather than the non-constructable shape
    /// WebIDL gives an operation. Under JSEAL that distinction is which factory is called, and this
    /// now calls the method factory: <c>el.beginElement.prototype</c> is <c>undefined</c> and
    /// <c>new el.beginElement()</c> throws, as a browser answers.
    /// </remarks>
    private static void InstallSmilNoOps(IJsRealm realm, JsValue obj)
    {
        realm.DefineMethod(obj, "beginElement", 0, static (in _) => JsValue.Undefined);

        realm.DefineMethod(obj, "endElement", 0, static (in _) => JsValue.Undefined);

        realm.DefineMethod(obj, "getStartTime", 0, static (in _) => JsValue.Number(0));
    }

    // SVGAnimatedLength stub for a dimensional presentation attribute — baseVal/animVal each an SVGLength.
    private static JsValue BuildAnimatedLength(IJsRealm realm, string attrName, DomElement element)
    {
        var animLength = realm.NewObject();
        var valueStr = DomBridgeUtils.TryGetAttribute(element, attrName, out var v) ? v : "0";
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
        if (DomBridgeUtils.TryGetAttribute(element, "viewBox", out var vb) && !string.IsNullOrWhiteSpace(vb))
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
        return JsValue.Number(element.TextContent.Length);
    }

    private static JsValue GetComputedTextLength(DomElement element)
    {
        var length = element.TextContent.Length;
        // Stub: estimate using font-size * character count * 0.6 average advance ratio
        var fontSize = ReadFontSize(element);
        return JsValue.Number(length * fontSize * 0.6);
    }

    private static JsValue GetSubStringLength(DomElement element, in JsCall call)
    {
        var length = element.TextContent.Length;
        // The realm's ToNumber, not the handle's: the engine's DoubleValue on an argument *was* the
        // ECMAScript coercion, so `getSubStringLength("1", "2")` has always counted from character 1,
        // and an argument object's valueOf has always been allowed to run here.
        var charnum = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        var nchars = call.Length > 1 ? (int)call.Realm.ToNumber(call[1]) : 0;
        if (charnum < 0 || charnum >= length)
            throw call.Realm.Error(JsErrorKind.Error, "INDEX_SIZE_ERR");
        if (nchars == 0)
            return JsValue.Number(0);
        var fontSize = ReadFontSize(element);
        return JsValue.Number(nchars * fontSize * 0.6);
    }

    private static JsValue GetStartPositionOfChar(DomElement element, in JsCall call)
    {
        var length = element.TextContent.Length;
        var charnum = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        if (charnum < 0 || charnum >= length)
            throw call.Realm.Error(JsErrorKind.Error, "INDEX_SIZE_ERR");
        var fontSize = ReadFontSize(element);

        var pt = call.Realm.NewObject();
        call.Realm.DefineValue(pt, "x", JsValue.Number(charnum * fontSize * 0.6));
        call.Realm.DefineValue(pt, "y", JsValue.Number(fontSize));
        return pt;
    }

    private static JsValue GetEndPositionOfChar(DomElement element, in JsCall call)
    {
        var length = element.TextContent.Length;
        var charnum = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        if (charnum < 0 || charnum >= length)
            throw call.Realm.Error(JsErrorKind.Error, "INDEX_SIZE_ERR");
        var fontSize = ReadFontSize(element);

        var pt = call.Realm.NewObject();
        call.Realm.DefineValue(pt, "x", JsValue.Number((charnum + 1) * fontSize * 0.6));
        call.Realm.DefineValue(pt, "y", JsValue.Number(fontSize));
        return pt;
    }

    private static JsValue GetRotationOfChar(DomElement element, in JsCall call)
    {
        var length = element.TextContent.Length;
        var charnum = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        if (charnum < 0 || charnum >= length)
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
        if (DomBridgeUtils.TryGetAttribute(element, "font-size", out var fs))
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
