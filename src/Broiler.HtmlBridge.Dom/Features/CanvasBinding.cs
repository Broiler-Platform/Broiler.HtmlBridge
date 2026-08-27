using System.Runtime.CompilerServices;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Array.Typed;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;
using Broiler.Graphics;
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
    private sealed record CanvasContext(JSObject JsObject, CanvasRenderingContext2D State);

    /// <summary>
    /// Installs the <c>HTMLCanvasElement</c> members on <paramref name="obj"/>. Called for every
    /// element, so it does nothing unless this one is a <c>&lt;canvas&gt;</c>: these names belong to
    /// that interface, and a page asking <c>'getContext' in el</c> or <c>el.width</c> must not find
    /// them on a <c>&lt;div&gt;</c>. <c>getContext</c> used to be installed unconditionally and
    /// answered <c>null</c> from a tag check inside — which made the *call* right and the *name*
    /// wrong, and <c>width</c>/<c>height</c> could not have been staged that way at all: they would
    /// have shadowed the reflected dimensions every other element has.
    /// </summary>
    public static void Install(ICanvasHost host, JSObject obj, DomElement element)
    {
        if (!string.Equals(element.TagName, "canvas", StringComparison.OrdinalIgnoreCase))
            return;

        obj.FastAddValue("getContext",
            new DomFunction((in a) => GetContext(host, obj, element, in a), "getContext", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("toDataURL",
            new DomFunction((in a) => ElementToDataUrl(host, obj, element, in a), "toDataURL", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // width/height are unsigned longs reflecting the content attributes, and assigning either
        // resets the bitmap. Without these the canvas kept whatever size it had when getContext was
        // first called, so a page that sized its canvas afterwards — or cleared it with the standard
        // `canvas.width = canvas.width` — drew into a bitmap of the wrong size and never cleared.
        InstallDimension(obj, element, "width", DefaultWidth);
        InstallDimension(obj, element, "height", DefaultHeight);
    }

    private const int DefaultWidth = 300;
    private const int DefaultHeight = 150;

    /// <summary>Largest rectangle <c>getImageData</c>/<c>createImageData</c> will allocate, in pixels.</summary>
    private const long MaxImageDataPixels = 64L * 1024 * 1024;

    private static void InstallDimension(JSObject obj, DomElement element, string name, int fallback)
    {
        obj.FastAddProperty(name,
            new DomFunction((in _) => new JSNumber(Dimension(element, name, fallback)), "get " + name),
            new DomFunction((in a) => SetDimension(element, name, in a), "set " + name),
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    private static int Dimension(DomElement element, string name, int fallback) =>
        DomBridge.TryGetAttribute(element, name, out var raw)
        && int.TryParse(raw, out var parsed)
        && parsed >= 0
            ? parsed
            : fallback;

    private static JSValue SetDimension(DomElement element, string name, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;

        // A value that is not a valid non-negative integer reverts to the default, per the reflection
        // rules for an unsigned long — it does not leave the previous value in place.
        int value = ToInt(a[0]);
        if (value < 0)
            value = name == "width" ? DefaultWidth : DefaultHeight;
        DomBridge.SetAttr(element, name, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (Contexts.TryGetValue(element, out var context))
        {
            context.State.ResetSurface(
                Dimension(element, "width", DefaultWidth),
                Dimension(element, "height", DefaultHeight));
        }

        return JSUndefined.Value;
    }

    // getContext(contextType) — returns the 2D context for a <canvas>, else null (only "2d" is supported).
    private static JSValue GetContext(ICanvasHost host, JSObject canvasObject, DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        // Any context type other than "2d" is one this engine does not implement, and null is what the
        // spec says to answer for an unsupported type.
        if (!string.Equals(a[0].ToString(), "2d", StringComparison.OrdinalIgnoreCase))
            return JSNull.Value;

        return GetOrCreateContext(host, canvasObject, element).JsObject;
    }

    private static CanvasContext GetOrCreateContext(ICanvasHost host, JSObject canvasObject, DomElement element)
    {
        if (Contexts.TryGetValue(element, out var existing))
            return existing;

        var created = BuildCanvas2DContext(host, canvasObject, element);
        // GetValue rather than Add: two lookups racing on the same element must agree on one context,
        // because the loser's bitmap is the one a page would silently keep drawing into.
        return Contexts.GetValue(element, _ => created);
    }

    /// <summary>
    /// <c>canvas.toDataURL(type, quality)</c> — on the element, not the context. A canvas that was never
    /// asked for a context still has a bitmap to serialize (a transparent one), which is why this does
    /// not require <c>getContext</c> to have been called first.
    /// </summary>
    private static JSValue ElementToDataUrl(ICanvasHost host, JSObject canvasObject, DomElement element, in Arguments a) =>
        ToDataUrl(GetOrCreateContext(host, canvasObject, element).State, in a);

    /// <summary>
    /// Builds the Canvas 2D rendering context: the drawing-state properties, the drawing methods that
    /// now rasterise into the context's bitmap, and the pixel APIs that read it back.
    /// </summary>
    private static CanvasContext BuildCanvas2DContext(ICanvasHost host, JSObject canvasObject, DomElement canvas)
    {
        var ctx = new JSObject();
        var context2d = new CanvasRenderingContext2D(
            Dimension(canvas, "width", DefaultWidth),
            Dimension(canvas, "height", DefaultHeight));

        // fillStyle (get/set)
        ctx.FastAddProperty("fillStyle",
            new DomFunction((in _) => new JSString(context2d.FillStyle), "get fillStyle"),
            new DomFunction((in a) => SetFillStyle(context2d, in a), "set fillStyle"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // strokeStyle (get/set)
        ctx.FastAddProperty("strokeStyle",
            new DomFunction((in _) => new JSString(context2d.StrokeStyle), "get strokeStyle"),
            new DomFunction((in a) => SetStrokeStyle(context2d, in a), "set strokeStyle"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // lineWidth (get/set)
        ctx.FastAddProperty("lineWidth",
            new DomFunction((in _) => new JSNumber(context2d.LineWidth), "get lineWidth"),
            new DomFunction((in a) => SetLineWidth(context2d, in a), "set lineWidth"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // font (get/set)
        ctx.FastAddProperty("font",
            new DomFunction((in _) => new JSString(context2d.Font), "get font"),
            new DomFunction((in a) => SetFont(context2d, in a), "set font"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // textAlign (get/set)
        ctx.FastAddProperty("textAlign",
            new DomFunction((in _) => new JSString(context2d.TextAlign), "get textAlign"),
            new DomFunction((in a) => SetTextAlign(context2d, in a), "set textAlign"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // globalAlpha (get/set)
        ctx.FastAddProperty("globalAlpha",
            new DomFunction((in _) => new JSNumber(context2d.GlobalAlpha), "get globalAlpha"),
            new DomFunction((in a) => SetGlobalAlpha(context2d, in a), "set globalAlpha"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // globalCompositeOperation (get/set) — a real accessor rather than the plain own property an
        // extensible object grew on assignment, so an operator the rasteriser cannot honour is rejected
        // at the setter (the spec's "ignore an invalid value") instead of round-tripping as if it had
        // been applied.
        ctx.FastAddProperty("globalCompositeOperation",
            new DomFunction((in _) => new JSString(context2d.GlobalCompositeOperation), "get globalCompositeOperation"),
            new DomFunction((in a) => SetGlobalCompositeOperation(context2d, in a), "set globalCompositeOperation"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // canvas — the element that owns this context. Was a fresh empty object, so ctx.canvas.width did
        // not answer and ctx.canvas === theCanvas was false.
        ctx.FastAddProperty("canvas",
            new DomFunction((in _) => canvasObject, "get canvas"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // Drawing methods
        ctx.FastAddValue("fillRect", new DomFunction((in a) => FillRect(context2d, in a), "fillRect", 4), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("strokeRect", new DomFunction((in a) => StrokeRect(context2d, in a), "strokeRect", 4), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("clearRect", new DomFunction((in a) => ClearRect(context2d, in a), "clearRect", 4), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("beginPath", new DomFunction((in _) => BeginPath(context2d, in _), "beginPath", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("moveTo", new DomFunction((in a) => MoveTo(context2d, in a), "moveTo", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("lineTo", new DomFunction((in a) => LineTo(context2d, in a), "lineTo", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("arc", new DomFunction((in a) => Arc(context2d, in a), "arc", 5), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("rect", new DomFunction((in a) => Rect(context2d, in a), "rect", 4), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("closePath", new DomFunction((in _) => ClosePath(context2d, in _), "closePath", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("fill", new DomFunction((in _) => Fill(context2d, in _), "fill", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("stroke", new DomFunction((in _) => Stroke(context2d, in _), "stroke", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("fillText", new DomFunction((in a) => FillText(context2d, in a), "fillText", 3), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("strokeText", new DomFunction((in a) => StrokeText(context2d, in a), "strokeText", 3), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("save", new DomFunction((in _) => Save(context2d, in _), "save", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("restore", new DomFunction((in _) => Restore(context2d, in _), "restore", 0), JSPropertyAttributes.EnumerableConfigurableValue);

        // measureText(text) — returns { width: ... }
        ctx.FastAddValue("measureText", new DomFunction((in a) => MeasureText(context2d, in a), "measureText", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        // Pixel access
        ctx.FastAddValue("getImageData", new DomFunction((in a) => GetImageData(context2d, host, in a), "getImageData", 4), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("putImageData", new DomFunction((in a) => PutImageData(context2d, in a), "putImageData", 3), JSPropertyAttributes.EnumerableConfigurableValue);

        ctx.FastAddValue("createImageData", new DomFunction((in a) => CreateImageData(host, in a), "createImageData", 2), JSPropertyAttributes.EnumerableConfigurableValue);

        // No toDataURL here: HTML puts it on HTMLCanvasElement only, and a page reaches it from a context
        // through ctx.canvas. Adding it to the context would be a name feature detection could trip on.
        return new CanvasContext(ctx, context2d);
    }

    // ---- drawing-state setters --------------------------------------------------------------------

    private static JSValue SetFillStyle(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length > 0)
            context2d.FillStyle = a[0].ToString();
        return JSUndefined.Value;
    }

    private static JSValue SetStrokeStyle(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length > 0)
            context2d.StrokeStyle = a[0].ToString();
        return JSUndefined.Value;
    }

    private static JSValue SetLineWidth(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length > 0 && a[0] is JSNumber n)
            context2d.LineWidth = (float)n.DoubleValue;
        return JSUndefined.Value;
    }

    private static JSValue SetFont(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length > 0)
            context2d.Font = a[0].ToString();
        return JSUndefined.Value;
    }

    private static JSValue SetTextAlign(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var value = a[0].ToString();
        if (value is "start" or "end" or "left" or "right" or "center")
            context2d.TextAlign = value;
        return JSUndefined.Value;
    }

    private static JSValue SetGlobalAlpha(CanvasRenderingContext2D context2d, in Arguments a)
    {
        // Out-of-range and non-finite values are ignored rather than clamped (HTML §canvas): the state
        // keeps its previous value.
        if (a.Length > 0 && a[0] is JSNumber n && n.DoubleValue is >= 0 and <= 1)
            context2d.GlobalAlpha = (float)n.DoubleValue;
        return JSUndefined.Value;
    }

    private static JSValue SetGlobalCompositeOperation(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length > 0 && CanvasRenderingContext2D.IsSupportedCompositeOperation(a[0].ToString()))
            context2d.GlobalCompositeOperation = a[0].ToString();
        return JSUndefined.Value;
    }

    // ---- drawing ----------------------------------------------------------------------------------

    private static JSValue FillRect(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length >= 4)
            context2d.FillRect((float)a[0].DoubleValue, (float)a[1].DoubleValue, (float)a[2].DoubleValue, (float)a[3].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue StrokeRect(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length >= 4)
            context2d.StrokeRect((float)a[0].DoubleValue, (float)a[1].DoubleValue, (float)a[2].DoubleValue, (float)a[3].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue ClearRect(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length >= 4)
            context2d.ClearRect((float)a[0].DoubleValue, (float)a[1].DoubleValue, (float)a[2].DoubleValue, (float)a[3].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue BeginPath(CanvasRenderingContext2D context2d, in Arguments _)
    {
        context2d.BeginPath();
        return JSUndefined.Value;
    }

    private static JSValue MoveTo(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length >= 2)
            context2d.MoveTo((float)a[0].DoubleValue, (float)a[1].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue LineTo(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length >= 2)
            context2d.LineTo((float)a[0].DoubleValue, (float)a[1].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue Arc(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length >= 5)
            context2d.Arc((float)a[0].DoubleValue, (float)a[1].DoubleValue, (float)a[2].DoubleValue, (float)a[3].DoubleValue, (float)a[4].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue Rect(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length >= 4)
        {
            float x = (float)a[0].DoubleValue, y = (float)a[1].DoubleValue;
            float w = (float)a[2].DoubleValue, h = (float)a[3].DoubleValue;
            context2d.MoveTo(x, y);
            context2d.LineTo(x + w, y);
            context2d.LineTo(x + w, y + h);
            context2d.LineTo(x, y + h);
            context2d.ClosePath();
        }
        return JSUndefined.Value;
    }

    private static JSValue ClosePath(CanvasRenderingContext2D context2d, in Arguments _)
    {
        context2d.ClosePath();
        return JSUndefined.Value;
    }

    private static JSValue Fill(CanvasRenderingContext2D context2d, in Arguments _)
    {
        context2d.Fill();
        return JSUndefined.Value;
    }

    private static JSValue Stroke(CanvasRenderingContext2D context2d, in Arguments _)
    {
        context2d.Stroke();
        return JSUndefined.Value;
    }

    private static JSValue FillText(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length >= 3)
            context2d.FillText(a[0].ToString(), (float)a[1].DoubleValue, (float)a[2].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue StrokeText(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length >= 3)
            context2d.StrokeText(a[0].ToString(), (float)a[1].DoubleValue, (float)a[2].DoubleValue);
        return JSUndefined.Value;
    }

    private static JSValue Save(CanvasRenderingContext2D context2d, in Arguments _)
    {
        context2d.Save();
        return JSUndefined.Value;
    }

    private static JSValue Restore(CanvasRenderingContext2D context2d, in Arguments _)
    {
        context2d.Restore();
        return JSUndefined.Value;
    }

    private static JSValue MeasureText(CanvasRenderingContext2D context2d, in Arguments a)
    {
        var text = a.Length > 0 ? a[0].ToString() : string.Empty;
        var result = new JSObject();
        result.FastAddValue("width", new JSNumber(context2d.MeasureTextWidth(text)), JSPropertyAttributes.EnumerableConfigurableValue);
        return result;
    }

    // ---- pixel access -----------------------------------------------------------------------------

    private static JSValue GetImageData(CanvasRenderingContext2D context2d, ICanvasHost host, in Arguments a)
    {
        if (a.Length < 4)
            throw new JSException(
                "Failed to execute 'getImageData' on 'CanvasRenderingContext2D': 4 arguments required.");

        int sx = ToInt(a[0]), sy = ToInt(a[1]);
        int width = ToInt(a[2]), height = ToInt(a[3]);
        // Negative extents address the rectangle in the other direction rather than being an error.
        if (width < 0) { sx += width; width = -width; }
        if (height < 0) { sy += height; height = -height; }
        if (width == 0 || height == 0)
            throw new JSException(
                "Failed to execute 'getImageData' on 'CanvasRenderingContext2D': the source rectangle is empty.");
        // The rectangle is not clipped to the canvas — out-of-bounds pixels read as transparent black —
        // so its size is bounded by the argument alone and a page can name one no allocation could hold.
        if ((long)width * height > MaxImageDataPixels)
            throw new JSException(
                "Failed to execute 'getImageData' on 'CanvasRenderingContext2D': the source rectangle is too large.");

        byte[] pixels = context2d.GetImageData(sx, sy, width, height);
        return BuildImageData(host, pixels, width, height);
    }

    private static JSValue PutImageData(CanvasRenderingContext2D context2d, in Arguments a)
    {
        if (a.Length < 3)
            throw new JSException(
                "Failed to execute 'putImageData' on 'CanvasRenderingContext2D': 3 arguments required.");
        if (a[0] is not JSObject imageData)
            throw new JSException(
                "Failed to execute 'putImageData' on 'CanvasRenderingContext2D': parameter 1 is not of type 'ImageData'.");

        int width = ToInt(imageData[(KeyString)"width"]);
        int height = ToInt(imageData[(KeyString)"height"]);
        if (width <= 0 || height <= 0)
            return JSUndefined.Value;

        byte[] pixels = ReadPixelBytes(imageData[(KeyString)"data"], width, height);
        context2d.PutImageData(pixels, width, height, ToInt(a[1]), ToInt(a[2]));
        return JSUndefined.Value;
    }

    // No context parameter: createImageData yields transparent black of the requested size and reads
    // nothing from the canvas it was called on.
    private static JSValue CreateImageData(ICanvasHost host, in Arguments a)
    {
        int width, height;
        if (a.Length >= 2)
        {
            // Magnitude, not Math.Abs: the sign is ignored here, and Math.Abs(int.MinValue) throws.
            width = Magnitude(ToInt(a[0]));
            height = Magnitude(ToInt(a[1]));
        }
        else if (a.Length == 1 && a[0] is JSObject source)
        {
            // createImageData(imagedata) — same dimensions, but transparent black rather than a copy.
            width = ToInt(source[(KeyString)"width"]);
            height = ToInt(source[(KeyString)"height"]);
        }
        else
        {
            throw new JSException(
                "Failed to execute 'createImageData' on 'CanvasRenderingContext2D': 2 arguments required.");
        }

        if (width == 0 || height == 0)
            throw new JSException(
                "Failed to execute 'createImageData' on 'CanvasRenderingContext2D': the source dimensions are zero.");
        if ((long)width * height > MaxImageDataPixels)
            throw new JSException(
                "Failed to execute 'createImageData' on 'CanvasRenderingContext2D': the dimensions are too large.");

        return BuildImageData(host, new byte[(long)width * height * 4], width, height);
    }

    private static int Magnitude(int value) => value == int.MinValue ? int.MaxValue : Math.Abs(value);

    /// <summary>
    /// Builds an <c>ImageData</c>: <c>width</c>, <c>height</c> and a <c>data</c> array holding
    /// straight-alpha RGBA. <c>data</c> is built through the realm's <c>Uint8ClampedArray</c> so it is a
    /// real typed array; a realm without one falls back to a plain array, which keeps indexing working
    /// where the alternative would be no <c>ImageData</c> at all.
    /// </summary>
    private static JSObject BuildImageData(ICanvasHost host, byte[] pixels, int width, int height)
    {
        var imageData = new JSObject();
        imageData.FastAddValue("width", new JSNumber(width), JSPropertyAttributes.EnumerableConfigurableValue);
        imageData.FastAddValue("height", new JSNumber(height), JSPropertyAttributes.EnumerableConfigurableValue);
        imageData.FastAddValue("data", BuildPixelArray(host, pixels), JSPropertyAttributes.EnumerableConfigurableValue);
        return imageData;
    }

    private static JSValue BuildPixelArray(ICanvasHost host, byte[] pixels)
    {
        if (host.WindowJSObject?[(KeyString)"Uint8ClampedArray"] is JSFunction clampedArrayCtor)
        {
            try
            {
                return clampedArrayCtor.CreateInstance(new Arguments(JSUndefined.Value, new JSArrayBuffer(pixels)));
            }
            catch
            {
                // Fall through to the plain array below.
            }
        }

        var values = new JSValue[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
            values[i] = new JSNumber(pixels[i]);
        return new JSArray(values);
    }

    /// <summary>
    /// Reads an <c>ImageData.data</c> back into bytes, whichever of the two shapes
    /// <see cref="BuildPixelArray"/> produced — and equally an <c>ImageData</c> the page built itself.
    /// </summary>
    private static byte[] ReadPixelBytes(JSValue? data, int width, int height)
    {
        var pixels = new byte[(long)width * height * 4];
        if (data is not JSObject source)
            return pixels;

        for (uint i = 0; i < pixels.Length; i++)
        {
            var value = source.GetValue(i, source, throwError: false);
            if (value is null || value.IsUndefined)
                continue;
            pixels[i] = (byte)Math.Clamp((int)Math.Round(value.DoubleValue), 0, 255);
        }

        return pixels;
    }

    // ---- serialization ----------------------------------------------------------------------------

    /// <summary>
    /// <c>toDataURL(type, quality)</c>. An image format the encoders do not support is <b>not</b> an
    /// error: HTML requires falling back to <c>image/png</c>, and the returned URL names the type that
    /// was actually produced — which is exactly how a feature detector tells the difference.
    /// </summary>
    private static JSValue ToDataUrl(CanvasRenderingContext2D context2d, in Arguments a)
    {
        // "data:," is the spec's answer for a canvas with no pixels to serialize.
        if (!context2d.HasBitmap || !BImageCodecs.IsRegistered)
            return new JSString("data:,");

        string requested = a.Length > 0 && !a[0].IsUndefined ? a[0].ToString() : "image/png";
        (ImageEncodeFormat format, string mediaType) = ResolveEncodeFormat(requested);

        int quality = 92;
        if (a.Length > 1 && a[1] is JSNumber q && q.DoubleValue is >= 0 and <= 1)
            quality = (int)Math.Round(q.DoubleValue * 100);

        byte[] encoded;
        try
        {
            encoded = context2d.Encode(format, quality);
        }
        catch (Exception)
        {
            // An encoder that cannot produce this bitmap leaves the canvas unserializable rather than
            // taking the page's script down with it.
            return new JSString("data:,");
        }

        return encoded.Length == 0
            ? new JSString("data:,")
            : new JSString($"data:{mediaType};base64,{Convert.ToBase64String(encoded)}");
    }

    private static (ImageEncodeFormat Format, string MediaType) ResolveEncodeFormat(string requested) =>
        requested.Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => (ImageEncodeFormat.Jpeg, "image/jpeg"),
            "image/bmp" => (ImageEncodeFormat.Bmp, "image/bmp"),
            "image/gif" => (ImageEncodeFormat.Gif, "image/gif"),
            _ => (ImageEncodeFormat.Png, "image/png"),
        };

    private static int ToInt(JSValue? value)
    {
        double number = value?.DoubleValue ?? 0;
        if (double.IsNaN(number) || double.IsInfinity(number))
            return 0;
        return (int)Math.Clamp(Math.Truncate(number), int.MinValue, int.MaxValue);
    }
}
