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
/// Text metrics and bounds use ISvgGeometryHost and the renderer font metrics. Character extents
/// describe advance cells, not glyph ink outlines; animated-length accessors remain attribute based.
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
    public static void Install(ISvgGeometryHost host, IJsRealm realm, JsValue obj, DomElement element, string tag)
    {
        // -- SVG DOM interfaces --

        // SVG element properties — provide SVGAnimatedLength stubs for dimensional attributes
        if (!(element.NamespaceUri == "http://www.w3.org/2000/svg" ||
              tag == "svg" || tag == "rect" || tag == "circle" || tag == "ellipse" ||
              tag == "line" || tag == "polyline" || tag == "polygon" || tag == "path" ||
              tag == "text" || tag == "g" || tag == "use" || tag == "image" ||
              tag == "svg:svg" || tag == "svg:rect" || tag == "svg:text" || tag == "svg:g"))
            return;

        realm.DefineMethod(obj, "getBBox", 0, (in call) =>
        {
            var rect = host.SvgBounds(element);
            return GeometryBinding.CreateDomRect(call.Realm, rect.X, rect.Y, rect.Width, rect.Height);
        });

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

            // getComputedTextLength() — returns measured total advance width
            realm.DefineMethod(obj, "getComputedTextLength", 0, (in _) =>
            {
                var text = host.SvgText(element);
                return JsValue.Number(text.Measure(text.Text));
            });

            // getSubStringLength(charnum, nchars) — returns advance width of substring
            realm.DefineMethod(obj, "getSubStringLength", 2, (in call) =>
            {
                var text = host.SvgText(element);
                var index = CharacterIndex(text, in call);
                var count = call.Length > 1 ? Math.Max(0, (int)call.Realm.ToNumber(call[1])) : 0;
                return JsValue.Number(text.Measure(text.Text.Substring(index, Math.Min(count, text.Text.Length - index))));
            });

            realm.DefineMethod(obj, "getExtentOfChar", 1, (in call) =>
            {
                var text = host.SvgText(element);
                var index = CharacterIndex(text, in call);
                var left = text.Measure(text.Text[..index]);
                var right = text.Measure(text.Text[..(index + 1)]);
                return GeometryBinding.CreateDomRect(call.Realm, text.X + left, text.Y - text.Baseline,
                    Math.Max(0, right - left), text.Height);
            });

            // getStartPositionOfChar(charnum) — returns SVGPoint {x, y}
            realm.DefineMethod(obj, "getStartPositionOfChar", 1,
                (in call) => TextPosition(host, element, false, in call));

            // getEndPositionOfChar(charnum) — returns SVGPoint {x, y}
            realm.DefineMethod(obj, "getEndPositionOfChar", 1, (in call) => TextPosition(host, element, true, in call));

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
    private static int CharacterIndex(SvgTextMeasurement text, in JsCall call)
    {
        if (call.Length == 0) throw call.Realm.Error(JsErrorKind.TypeError, "A character index is required.");
        var value = call.Realm.ToNumber(call[0]);
        var index = double.IsFinite(value) ? (long)Math.Truncate(value) : 0;
        if (index < 0 || index >= text.Text.Length) throw call.Realm.DomError("IndexSizeError", "Character index is out of range.");
        return (int)index;
    }

    private static JsValue TextPosition(ISvgGeometryHost host, DomElement element, bool end, in JsCall call)
    {
        var text = host.SvgText(element);
        var index = CharacterIndex(text, in call) + (end ? 1 : 0);
        var point = call.Realm.NewObject();
        call.Realm.DefineValue(point, "x", JsValue.Number(text.X + text.Measure(text.Text[..index])));
        call.Realm.DefineValue(point, "y", JsValue.Number(text.Y));
        return point;
    }

    private static void InstallSmilNoOps(IJsRealm realm, JsValue obj)
    {
        realm.DefineMethod(obj, "beginElement", 0, static (in _) => JsValue.Undefined);

        realm.DefineMethod(obj, "endElement", 0, static (in _) => JsValue.Undefined);

        realm.DefineMethod(obj, "getStartTime", 0, static (in _) => JsValue.Number(0));
    }

    /// <summary>
    /// The one place this module turns an attribute into a number, so a value it cannot represent
    /// is refused once rather than in each of the six reads below.
    /// </summary>
    /// <remarks>
    /// <c>NumberStyles.Any</c> accepts .NET's symbolic <c>NaN</c> and <c>Infinity</c>, and it
    /// overflows a double on an exponent — or on a long enough run of digits — with no symbol in
    /// the attribute at all, so <c>&lt;rect width="1e400"&gt;</c> answered
    /// <c>rect.width.baseVal.value === Infinity</c> to a page. The geometry side of the same
    /// attribute refuses that (<c>ResolveSvgLength</c> reads it through
    /// <c>DomBridgeUtils.TryParseFiniteScalar</c>), and this IDL stub is the other reader; the two
    /// disagreeing is what made it a route rather than a duplicate.
    /// <para>
    /// Zero is not a substitution invented here: it is what every one of these reads already
    /// answers for an attribute it cannot parse — an absent one, <c>"junk"</c>, a percentage —
    /// because a failed <see cref="double.TryParse(string, NumberStyles, IFormatProvider, out double)"/>
    /// leaves its result at zero. An unrepresentable value now takes that same path.
    /// </para>
    /// </remarks>
    private static double ParseFiniteAttributeNumber(string? text) =>
        double.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) &&
        double.IsFinite(value)
            ? value
            : 0;

    // SVGAnimatedLength stub for a dimensional presentation attribute — baseVal/animVal each an SVGLength.
    private static JsValue BuildAnimatedLength(IJsRealm realm, string attrName, DomElement element)
    {
        var animLength = realm.NewObject();
        var valueStr = DomBridgeUtils.TryGetAttribute(element, attrName, out var v) ? v : "0";
        var numVal = ParseFiniteAttributeNumber(valueStr);
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
                vbX = ParseFiniteAttributeNumber(parts[0]);
                vbY = ParseFiniteAttributeNumber(parts[1]);
                vbW = ParseFiniteAttributeNumber(parts[2]);
                vbH = ParseFiniteAttributeNumber(parts[3]);
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
