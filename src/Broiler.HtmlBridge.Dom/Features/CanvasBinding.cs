using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.Graphics;
using Broiler.HtmlBridge.Jseal;
using Broiler.Media.Image;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTML <c>canvas.getContext("2d")</c> binding and its 2D drawing context, co-located as an HtmlBridge
/// feature module (Phase 3): <c>getContext</c> resolves a <c>&lt;canvas&gt;</c> element to a
/// <see cref="CanvasRenderingContext2D"/>-backed JS object exposing the drawing-state properties, the
/// <c>save</c>/<c>restore</c> state stack, the drawing methods, and the pixel APIs
/// (<c>getImageData</c>/<c>putImageData</c>/<c>createImageData</c>/<c>toDataURL</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>One context per canvas.</b> <c>getContext</c> returns the <em>same</em> object every call — the
/// spec requires it, and with a real backing store it also decides whether anything drawn survives:
/// building a fresh context per call handed back a blank bitmap each time. The context is keyed off the
/// element rather than off its JS wrapper so it survives the wrapper being rebuilt.
/// </para>
/// <para>
/// The <c>#if BROILER_CLI</c> guard that claimed to make <c>getContext("2d")</c> return <c>null</c> in
/// the CLI is gone, and removing it changed no behaviour: <c>BROILER_CLI</c> is defined by
/// <c>Broiler.Cli.csproj</c> for <em>its own</em> compilation, and a <c>DefineConstants</c> does not
/// reach a referenced project, so the constant was never set while this assembly was compiled and the
/// guarded branch was unreachable. What it cost was accuracy — it read as a live host difference that
/// did not exist, which is worse than no comment.
/// </para>
/// <para>
/// <b>The JavaScript vocabulary is JSEAL's</b> (<see cref="IJsRealm"/>): objects, accessors, methods,
/// coercions and errors all come from the realm, which is handed in when the members are installed and
/// arrives on the call frame for every body afterwards. <b>One line is the exception</b> and it is
/// named rather than hidden — <see cref="PixelBuffer"/>. JSEAL can mint an object, an array and a
/// function; it cannot mint an <c>ArrayBuffer</c>, and an <c>ImageData.data</c> that does not share one
/// with the bytes just read back would have to be copied element by element into a plain array. That
/// path exists as the fallback below and costs a boxed handle per <em>byte</em> — 24 bytes for each one
/// — so it is what a realm without <c>Uint8ClampedArray</c> gets and not what every
/// <c>getImageData</c> pays. The contract gap is real and worth closing; until it is, the seam is one
/// constructor call wide.
/// </para>
/// </remarks>
internal static class CanvasBinding
{
    // Keyed off the element: a DomElement can be handed a new JS wrapper (a re-projection, a re-entry
    // through a different accessor), and the pixels a page has already drawn must not depend on that.
    private static readonly ConditionalWeakTable<DomElement, CanvasContext> Contexts = new();

    /// <summary>
    /// The JS context object a page holds and the C# context that owns its bitmap, kept together so the
    /// element's own <c>toDataURL</c> can reach the pixels the context's drawing calls wrote.
    /// </summary>
    private sealed record CanvasContext(JsValue JsObject, CanvasRenderingContext2D State);

    /// <summary>
    /// Installs the <c>HTMLCanvasElement</c> members on <paramref name="obj"/>. Called for every
    /// element, so it does nothing unless this one is a <c>&lt;canvas&gt;</c>: these names belong to
    /// that interface, and a page asking <c>'getContext' in el</c> or <c>el.width</c> must not find
    /// them on a <c>&lt;div&gt;</c>. <c>getContext</c> used to be installed unconditionally and
    /// answered <c>null</c> from a tag check inside — which made the *call* right and the *name*
    /// wrong, and <c>width</c>/<c>height</c> could not have been staged that way at all: they would
    /// have shadowed the reflected dimensions every other element has.
    /// </summary>
    /// <param name="realm">The realm the installed members and everything they build belong to.</param>
    /// <param name="host">The bridge, for the window the pixel APIs read their constructor off.</param>
    /// <param name="obj">The element's JS wrapper.</param>
    /// <param name="element">The element the members read.</param>
    public static void Install(IJsRealm realm, ICanvasHost host, JsValue obj, DomElement element)
    {
        if (!string.Equals(element.TagName, "canvas", StringComparison.OrdinalIgnoreCase))
            return;

        realm.DefineValue(obj, "getContext",
            realm.NewMethod("getContext", (in call) => GetContext(host, obj, element, in call), 1));

        realm.DefineValue(obj, "toDataURL",
            realm.NewMethod("toDataURL", (in call) => ElementToDataUrl(host, obj, element, in call), 2));

        // width/height are unsigned longs reflecting the content attributes, and assigning either
        // resets the bitmap. Without these the canvas kept whatever size it had when getContext was
        // first called, so a page that sized its canvas afterwards — or cleared it with the standard
        // `canvas.width = canvas.width` — drew into a bitmap of the wrong size and never cleared.
        InstallDimension(realm, obj, element, "width", DefaultWidth);
        InstallDimension(realm, obj, element, "height", DefaultHeight);
    }

    private const int DefaultWidth = 300;
    private const int DefaultHeight = 150;

    /// <summary>Largest rectangle <c>getImageData</c>/<c>createImageData</c> will allocate, in pixels.</summary>
    private const long MaxImageDataPixels = 64L * 1024 * 1024;

    private static void InstallDimension(IJsRealm realm, JsValue obj, DomElement element, string name, int fallback)
    {
        // The realm mints both accessor functions and names them "get width"/"set width" itself, which
        // is what the two hand-built native accessors at this site were doing.
        realm.DefineAccessor(obj, name,
            (in _) => JsValue.Number(Dimension(element, name, fallback)),
            (in call) => SetDimension(element, name, in call));
    }

    private static int Dimension(DomElement element, string name, int fallback) =>
        DomBridge.TryGetAttribute(element, name, out var raw)
        && int.TryParse(raw, out var parsed)
        && parsed >= 0
            ? parsed
            : fallback;

    private static JsValue SetDimension(DomElement element, string name, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;

        // A value that is not a valid non-negative integer reverts to the default, per the reflection
        // rules for an unsigned long — it does not leave the previous value in place.
        int value = ToInt(call.Realm, call[0]);
        if (value < 0)
            value = name == "width" ? DefaultWidth : DefaultHeight;
        DomBridge.SetAttr(element, name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (Contexts.TryGetValue(element, out var context))
        {
            context.State.ResetSurface(
                Dimension(element, "width", DefaultWidth),
                Dimension(element, "height", DefaultHeight));
        }

        return JsValue.Undefined;
    }

    // getContext(contextType) — returns the 2D context for a <canvas>, else null (only "2d" is supported).
    private static JsValue GetContext(ICanvasHost host, JsValue canvasObject, DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        // Any context type other than "2d" is one this engine does not implement, and null is what the
        // spec says to answer for an unsupported type. ToJsString, not the handle's rendering: an
        // object argument must run its own toString, which is the coercion a page observes here.
        if (!string.Equals(call.Realm.ToJsString(call[0]), "2d", StringComparison.OrdinalIgnoreCase))
            return JsValue.Null;

        return GetOrCreateContext(call.Realm, host, canvasObject, element).JsObject;
    }

    private static CanvasContext GetOrCreateContext(IJsRealm realm, ICanvasHost host, JsValue canvasObject, DomElement element)
    {
        if (Contexts.TryGetValue(element, out var existing))
            return existing;

        var created = BuildCanvas2DContext(realm, host, canvasObject, element);
        // GetValue rather than Add: two lookups racing on the same element must agree on one context,
        // because the loser's bitmap is the one a page would silently keep drawing into.
        return Contexts.GetValue(element, _ => created);
    }

    /// <summary>
    /// <c>canvas.toDataURL(type, quality)</c> — on the element, not the context. A canvas that was never
    /// asked for a context still has a bitmap to serialize (a transparent one), which is why this does
    /// not require <c>getContext</c> to have been called first.
    /// </summary>
    private static JsValue ElementToDataUrl(ICanvasHost host, JsValue canvasObject, DomElement element, in JsCall call) =>
        ToDataUrl(GetOrCreateContext(call.Realm, host, canvasObject, element).State, in call);

    /// <summary>
    /// Builds the Canvas 2D rendering context: the drawing-state properties, the drawing methods that
    /// now rasterise into the context's bitmap, and the pixel APIs that read it back.
    /// </summary>
    private static CanvasContext BuildCanvas2DContext(IJsRealm realm, ICanvasHost host, JsValue canvasObject, DomElement canvas)
    {
        var ctx = realm.NewObject();
        var context2d = new CanvasRenderingContext2D(
            Dimension(canvas, "width", DefaultWidth),
            Dimension(canvas, "height", DefaultHeight));

        // fillStyle (get/set)
        realm.DefineAccessor(ctx, "fillStyle",
            (in _) => JsValue.String(context2d.FillStyle),
            (in call) => SetFillStyle(context2d, in call));

        // strokeStyle (get/set)
        realm.DefineAccessor(ctx, "strokeStyle",
            (in _) => JsValue.String(context2d.StrokeStyle),
            (in call) => SetStrokeStyle(context2d, in call));

        // lineWidth (get/set)
        realm.DefineAccessor(ctx, "lineWidth",
            (in _) => JsValue.Number(context2d.LineWidth),
            (in call) => SetLineWidth(context2d, in call));

        // font (get/set)
        realm.DefineAccessor(ctx, "font",
            (in _) => JsValue.String(context2d.Font),
            (in call) => SetFont(context2d, in call));

        // textAlign (get/set)
        realm.DefineAccessor(ctx, "textAlign",
            (in _) => JsValue.String(context2d.TextAlign),
            (in call) => SetTextAlign(context2d, in call));

        // globalAlpha (get/set)
        realm.DefineAccessor(ctx, "globalAlpha",
            (in _) => JsValue.Number(context2d.GlobalAlpha),
            (in call) => SetGlobalAlpha(context2d, in call));

        // globalCompositeOperation (get/set) — a real accessor rather than the plain own property an
        // extensible object grew on assignment, so an operator the rasteriser cannot honour is rejected
        // at the setter (the spec's "ignore an invalid value") instead of round-tripping as if it had
        // been applied.
        realm.DefineAccessor(ctx, "globalCompositeOperation",
            (in _) => JsValue.String(context2d.GlobalCompositeOperation),
            (in call) => SetGlobalCompositeOperation(context2d, in call));

        // canvas — the element that owns this context. Was a fresh empty object, so ctx.canvas.width did
        // not answer and ctx.canvas === theCanvas was false. A null setter is how a read-only IDL
        // attribute is spelled.
        realm.DefineAccessor(ctx, "canvas", (in _) => canvasObject, null);

        // Drawing methods
        realm.DefineValue(ctx, "fillRect", realm.NewMethod("fillRect", (in call) => FillRect(context2d, in call), 4));

        realm.DefineValue(ctx, "strokeRect", realm.NewMethod("strokeRect", (in call) => StrokeRect(context2d, in call), 4));

        realm.DefineValue(ctx, "clearRect", realm.NewMethod("clearRect", (in call) => ClearRect(context2d, in call), 4));

        realm.DefineValue(ctx, "beginPath", realm.NewMethod("beginPath", (in call) => BeginPath(context2d, in call)));

        realm.DefineValue(ctx, "moveTo", realm.NewMethod("moveTo", (in call) => MoveTo(context2d, in call), 2));

        realm.DefineValue(ctx, "lineTo", realm.NewMethod("lineTo", (in call) => LineTo(context2d, in call), 2));

        realm.DefineValue(ctx, "arc", realm.NewMethod("arc", (in call) => Arc(context2d, in call), 5));

        realm.DefineValue(ctx, "rect", realm.NewMethod("rect", (in call) => Rect(context2d, in call), 4));

        realm.DefineValue(ctx, "closePath", realm.NewMethod("closePath", (in call) => ClosePath(context2d, in call)));

        realm.DefineValue(ctx, "fill", realm.NewMethod("fill", (in call) => Fill(context2d, in call)));

        realm.DefineValue(ctx, "stroke", realm.NewMethod("stroke", (in call) => Stroke(context2d, in call)));

        realm.DefineValue(ctx, "fillText", realm.NewMethod("fillText", (in call) => FillText(context2d, in call), 3));

        realm.DefineValue(ctx, "strokeText", realm.NewMethod("strokeText", (in call) => StrokeText(context2d, in call), 3));

        realm.DefineValue(ctx, "save", realm.NewMethod("save", (in call) => Save(context2d, in call)));

        realm.DefineValue(ctx, "restore", realm.NewMethod("restore", (in call) => Restore(context2d, in call)));

        // measureText(text) — returns { width: ... }
        realm.DefineValue(ctx, "measureText", realm.NewMethod("measureText", (in call) => MeasureText(context2d, in call), 1));

        // Pixel access
        realm.DefineValue(ctx, "getImageData", realm.NewMethod("getImageData", (in call) => GetImageData(context2d, host, in call), 4));

        realm.DefineValue(ctx, "putImageData", realm.NewMethod("putImageData", (in call) => PutImageData(context2d, in call), 3));

        realm.DefineValue(ctx, "createImageData", realm.NewMethod("createImageData", (in call) => CreateImageData(host, in call), 2));

        // No toDataURL here: HTML puts it on HTMLCanvasElement only, and a page reaches it from a context
        // through ctx.canvas. Adding it to the context would be a name feature detection could trip on.
        return new CanvasContext(ctx, context2d);
    }

    // ---- drawing-state setters --------------------------------------------------------------------

    private static JsValue SetFillStyle(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length > 0)
            context2d.FillStyle = call.Realm.ToJsString(call[0]);
        return JsValue.Undefined;
    }

    private static JsValue SetStrokeStyle(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length > 0)
            context2d.StrokeStyle = call.Realm.ToJsString(call[0]);
        return JsValue.Undefined;
    }

    private static JsValue SetLineWidth(CanvasRenderingContext2D context2d, in JsCall call)
    {
        // A type test, not a coercion: only an actual JS number moves the pen width, so `ctx.lineWidth
        // = "4"` is ignored exactly as it was before.
        if (call.Length > 0 && call[0].IsNumber)
            context2d.LineWidth = (float)call[0].AsNumber;
        return JsValue.Undefined;
    }

    private static JsValue SetFont(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length > 0)
            context2d.Font = call.Realm.ToJsString(call[0]);
        return JsValue.Undefined;
    }

    private static JsValue SetTextAlign(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var value = call.Realm.ToJsString(call[0]);
        if (value is "start" or "end" or "left" or "right" or "center")
            context2d.TextAlign = value;
        return JsValue.Undefined;
    }

    private static JsValue SetGlobalAlpha(CanvasRenderingContext2D context2d, in JsCall call)
    {
        // Out-of-range and non-finite values are ignored rather than clamped (HTML §canvas): the state
        // keeps its previous value. As with lineWidth this is a type test rather than a coercion.
        if (call.Length > 0 && call[0].IsNumber && call[0].AsNumber is >= 0 and <= 1)
            context2d.GlobalAlpha = (float)call[0].AsNumber;
        return JsValue.Undefined;
    }

    private static JsValue SetGlobalCompositeOperation(CanvasRenderingContext2D context2d, in JsCall call)
    {
        // Coerced twice, as it always was: an object argument whose toString answers differently on the
        // second call sets a value the check did not approve, and reproducing that is what "preserve the
        // behaviour" means here.
        if (call.Length > 0 && CanvasRenderingContext2D.IsSupportedCompositeOperation(call.Realm.ToJsString(call[0])))
            context2d.GlobalCompositeOperation = call.Realm.ToJsString(call[0]);
        return JsValue.Undefined;
    }

    // ---- drawing ----------------------------------------------------------------------------------

    private static JsValue FillRect(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length >= 4)
            context2d.FillRect(Coordinate(call, 0), Coordinate(call, 1), Coordinate(call, 2), Coordinate(call, 3));
        return JsValue.Undefined;
    }

    private static JsValue StrokeRect(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length >= 4)
            context2d.StrokeRect(Coordinate(call, 0), Coordinate(call, 1), Coordinate(call, 2), Coordinate(call, 3));
        return JsValue.Undefined;
    }

    private static JsValue ClearRect(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length >= 4)
            context2d.ClearRect(Coordinate(call, 0), Coordinate(call, 1), Coordinate(call, 2), Coordinate(call, 3));
        return JsValue.Undefined;
    }

    private static JsValue BeginPath(CanvasRenderingContext2D context2d, in JsCall _)
    {
        context2d.BeginPath();
        return JsValue.Undefined;
    }

    private static JsValue MoveTo(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length >= 2)
            context2d.MoveTo(Coordinate(call, 0), Coordinate(call, 1));
        return JsValue.Undefined;
    }

    private static JsValue LineTo(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length >= 2)
            context2d.LineTo(Coordinate(call, 0), Coordinate(call, 1));
        return JsValue.Undefined;
    }

    private static JsValue Arc(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length >= 5)
        {
            context2d.Arc(
                Coordinate(call, 0), Coordinate(call, 1), Coordinate(call, 2),
                Coordinate(call, 3), Coordinate(call, 4));
        }
        return JsValue.Undefined;
    }

    private static JsValue Rect(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length >= 4)
        {
            float x = Coordinate(call, 0), y = Coordinate(call, 1);
            float w = Coordinate(call, 2), h = Coordinate(call, 3);
            context2d.MoveTo(x, y);
            context2d.LineTo(x + w, y);
            context2d.LineTo(x + w, y + h);
            context2d.LineTo(x, y + h);
            context2d.ClosePath();
        }
        return JsValue.Undefined;
    }

    private static JsValue ClosePath(CanvasRenderingContext2D context2d, in JsCall _)
    {
        context2d.ClosePath();
        return JsValue.Undefined;
    }

    private static JsValue Fill(CanvasRenderingContext2D context2d, in JsCall _)
    {
        context2d.Fill();
        return JsValue.Undefined;
    }

    private static JsValue Stroke(CanvasRenderingContext2D context2d, in JsCall _)
    {
        context2d.Stroke();
        return JsValue.Undefined;
    }

    private static JsValue FillText(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length >= 3)
            context2d.FillText(call.Realm.ToJsString(call[0]), Coordinate(call, 1), Coordinate(call, 2));
        return JsValue.Undefined;
    }

    private static JsValue StrokeText(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length >= 3)
            context2d.StrokeText(call.Realm.ToJsString(call[0]), Coordinate(call, 1), Coordinate(call, 2));
        return JsValue.Undefined;
    }

    private static JsValue Save(CanvasRenderingContext2D context2d, in JsCall _)
    {
        context2d.Save();
        return JsValue.Undefined;
    }

    private static JsValue Restore(CanvasRenderingContext2D context2d, in JsCall _)
    {
        context2d.Restore();
        return JsValue.Undefined;
    }

    private static JsValue MeasureText(CanvasRenderingContext2D context2d, in JsCall call)
    {
        var text = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        var result = call.Realm.NewObject();
        call.Realm.DefineValue(result, "width", JsValue.Number(context2d.MeasureTextWidth(text)));
        return result;
    }

    /// <summary>
    /// A drawing coordinate: the ECMAScript <c>ToNumber</c> of the argument, because every one of these
    /// is a place a page may legitimately hand a string (<c>ctx.fillRect(x, y, "100", "50")</c>).
    /// </summary>
    private static float Coordinate(in JsCall call, int index) => (float)call.Realm.ToNumber(call[index]);

    // ---- pixel access -----------------------------------------------------------------------------

    private static JsValue GetImageData(CanvasRenderingContext2D context2d, ICanvasHost host, in JsCall call)
    {
        if (call.Length < 4)
            throw call.Realm.Error(JsErrorKind.Error,
                "Failed to execute 'getImageData' on 'CanvasRenderingContext2D': 4 arguments required.");

        int sx = ToInt(call.Realm, call[0]), sy = ToInt(call.Realm, call[1]);
        int width = ToInt(call.Realm, call[2]), height = ToInt(call.Realm, call[3]);
        // Negative extents address the rectangle in the other direction rather than being an error.
        if (width < 0) { sx += width; width = -width; }
        if (height < 0) { sy += height; height = -height; }
        if (width == 0 || height == 0)
            throw call.Realm.Error(JsErrorKind.Error,
                "Failed to execute 'getImageData' on 'CanvasRenderingContext2D': the source rectangle is empty.");
        // The rectangle is not clipped to the canvas — out-of-bounds pixels read as transparent black —
        // so its size is bounded by the argument alone and a page can name one no allocation could hold.
        if ((long)width * height > MaxImageDataPixels)
            throw call.Realm.Error(JsErrorKind.Error,
                "Failed to execute 'getImageData' on 'CanvasRenderingContext2D': the source rectangle is too large.");

        byte[] pixels = context2d.GetImageData(sx, sy, width, height);
        return BuildImageData(call.Realm, host, pixels, width, height);
    }

    private static JsValue PutImageData(CanvasRenderingContext2D context2d, in JsCall call)
    {
        if (call.Length < 3)
            throw call.Realm.Error(JsErrorKind.Error,
                "Failed to execute 'putImageData' on 'CanvasRenderingContext2D': 3 arguments required.");
        if (!call[0].IsObject)
            throw call.Realm.Error(JsErrorKind.Error,
                "Failed to execute 'putImageData' on 'CanvasRenderingContext2D': parameter 1 is not of type 'ImageData'.");

        var imageData = call[0];
        int width = ToInt(call.Realm, call.Realm.GetProperty(imageData, "width"));
        int height = ToInt(call.Realm, call.Realm.GetProperty(imageData, "height"));
        if (width <= 0 || height <= 0)
            return JsValue.Undefined;

        byte[] pixels = ReadPixelBytes(call.Realm, call.Realm.GetProperty(imageData, "data"), width, height);
        context2d.PutImageData(pixels, width, height, ToInt(call.Realm, call[1]), ToInt(call.Realm, call[2]));
        return JsValue.Undefined;
    }

    // No context parameter: createImageData yields transparent black of the requested size and reads
    // nothing from the canvas it was called on.
    private static JsValue CreateImageData(ICanvasHost host, in JsCall call)
    {
        int width, height;
        if (call.Length >= 2)
        {
            // Magnitude, not Math.Abs: the sign is ignored here, and Math.Abs(int.MinValue) throws.
            width = Magnitude(ToInt(call.Realm, call[0]));
            height = Magnitude(ToInt(call.Realm, call[1]));
        }
        else if (call.Length == 1 && call[0].IsObject)
        {
            // createImageData(imagedata) — same dimensions, but transparent black rather than a copy.
            var source = call[0];
            width = ToInt(call.Realm, call.Realm.GetProperty(source, "width"));
            height = ToInt(call.Realm, call.Realm.GetProperty(source, "height"));
        }
        else
        {
            throw call.Realm.Error(JsErrorKind.Error,
                "Failed to execute 'createImageData' on 'CanvasRenderingContext2D': 2 arguments required.");
        }

        if (width == 0 || height == 0)
            throw call.Realm.Error(JsErrorKind.Error,
                "Failed to execute 'createImageData' on 'CanvasRenderingContext2D': the source dimensions are zero.");
        if ((long)width * height > MaxImageDataPixels)
            throw call.Realm.Error(JsErrorKind.Error,
                "Failed to execute 'createImageData' on 'CanvasRenderingContext2D': the dimensions are too large.");

        return BuildImageData(call.Realm, host, new byte[(long)width * height * 4], width, height);
    }

    private static int Magnitude(int value) => value == int.MinValue ? int.MaxValue : Math.Abs(value);

    /// <summary>
    /// Builds an <c>ImageData</c>: <c>width</c>, <c>height</c> and a <c>data</c> array holding
    /// straight-alpha RGBA. <c>data</c> is built through the realm's <c>Uint8ClampedArray</c> so it is a
    /// real typed array; a realm without one falls back to a plain array, which keeps indexing working
    /// where the alternative would be no <c>ImageData</c> at all.
    /// </summary>
    private static JsValue BuildImageData(IJsRealm realm, ICanvasHost host, byte[] pixels, int width, int height)
    {
        var imageData = realm.NewObject();
        realm.DefineValue(imageData, "width", JsValue.Number(width));
        realm.DefineValue(imageData, "height", JsValue.Number(height));
        realm.DefineValue(imageData, "data", BuildPixelArray(realm, host, pixels));
        return imageData;
    }

    private static JsValue BuildPixelArray(IJsRealm realm, ICanvasHost host, byte[] pixels)
    {
        var window = host.Window;
        if (window.IsObject && realm.GetProperty(window, "Uint8ClampedArray") is { IsFunction: true } clampedArrayCtor)
        {
            try
            {
                return realm.Construct(clampedArrayCtor, [realm.NewArrayBuffer(pixels)]);
            }
            catch
            {
                // Fall through to the plain array below.
            }
        }

        var values = new JsValue[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
            values[i] = JsValue.Number(pixels[i]);
        return realm.NewArray(values);
    }

    /// <summary>
    /// Reads an <c>ImageData.data</c> back into bytes, whichever of the two shapes
    /// <see cref="BuildPixelArray"/> produced — and equally an <c>ImageData</c> the page built itself.
    /// </summary>
    private static byte[] ReadPixelBytes(IJsRealm realm, JsValue data, int width, int height)
    {
        var pixels = new byte[(long)width * height * 4];
        if (!data.IsObject)
            return pixels;

        for (uint i = 0; i < pixels.Length; i++)
        {
            var value = realm.GetIndex(data, i);
            if (value.IsMissing || value.IsUndefined)
                continue;
            // A typed array answers numbers, which need no engine entry; anything else is a page-built
            // ImageData whose elements go through the ECMAScript coercion, as they did before.
            double number = value.IsNumber ? value.AsNumber : realm.ToNumber(value);
            pixels[i] = (byte)Math.Clamp((int)Math.Round(number), 0, 255);
        }

        return pixels;
    }

    // ---- serialization ----------------------------------------------------------------------------

    /// <summary>
    /// <c>toDataURL(type, quality)</c>. An image format the encoders do not support is <b>not</b> an
    /// error: HTML requires falling back to <c>image/png</c>, and the returned URL names the type that
    /// was actually produced — which is exactly how a feature detector tells the difference.
    /// </summary>
    private static JsValue ToDataUrl(CanvasRenderingContext2D context2d, in JsCall call)
    {
        // "data:," is the spec's answer for a canvas with no pixels to serialize.
        if (!context2d.HasBitmap || !BImageCodecs.IsRegistered)
            return JsValue.String("data:,");

        string requested = call.Length > 0 && !call[0].IsUndefined
            ? call.Realm.ToJsString(call[0])
            : "image/png";
        (ImageEncodeFormat format, string mediaType) = ResolveEncodeFormat(requested);

        int quality = 92;
        if (call.Length > 1 && call[1].IsNumber && call[1].AsNumber is >= 0 and <= 1)
            quality = (int)Math.Round(call[1].AsNumber * 100);

        byte[] encoded;
        try
        {
            encoded = context2d.Encode(format, quality);
        }
        catch (Exception)
        {
            // An encoder that cannot produce this bitmap leaves the canvas unserializable rather than
            // taking the page's script down with it.
            return JsValue.String("data:,");
        }

        return encoded.Length == 0
            ? JsValue.String("data:,")
            : JsValue.String($"data:{mediaType};base64,{Convert.ToBase64String(encoded)}");
    }

    private static (ImageEncodeFormat Format, string MediaType) ResolveEncodeFormat(string requested) =>
        requested.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => (ImageEncodeFormat.Jpeg, "image/jpeg"),
            "image/bmp" => (ImageEncodeFormat.Bmp, "image/bmp"),
            "image/gif" => (ImageEncodeFormat.Gif, "image/gif"),
            _ => (ImageEncodeFormat.Png, "image/png"),
        };

    /// <summary>
    /// An integer argument, truncated the way the canvas APIs take one. <c>Missing</c> is zero without
    /// entering the engine — the CLR null the argument frame used to answer past the end took the same
    /// branch, and <c>ToNumber</c> of a value that was never supplied is not a question the realm
    /// should be asked.
    /// </summary>
    private static int ToInt(IJsRealm realm, JsValue value)
    {
        if (value.IsMissing)
            return 0;

        double number = realm.ToNumber(value);
        if (double.IsNaN(number) || double.IsInfinity(number))
            return 0;
        return (int)Math.Clamp(Math.Truncate(number), int.MinValue, int.MaxValue);
    }
}
