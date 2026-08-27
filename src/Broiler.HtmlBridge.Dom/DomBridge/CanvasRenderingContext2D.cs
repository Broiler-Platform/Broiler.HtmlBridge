using System.Drawing;
using Broiler.CSS;
using Broiler.Graphics;
using Broiler.Media.Image;

namespace Broiler.HtmlBridge;

/// <summary>
/// The HTML5 Canvas 2D context that backs the <c>canvas.getContext("2d")</c> binding (see the
/// <c>CanvasBinding</c> feature module — Phase 3 P3.64 — which builds the context and its drawing
/// callbacks). Internal to the Canvas binding.
/// </summary>
/// <remarks>
/// <para>
/// <b>This context has pixels.</b> It owns a <see cref="BBitmap"/> the size of the canvas and rasterises
/// every drawing call into it through <see cref="BCanvas"/>, so <c>getImageData</c> and <c>toDataURL</c>
/// report what was actually drawn.
/// </para>
/// <para>
/// It has not always. Phase 6 (P6.1) removed a <c>CanvasDrawCommand</c> recorder that no renderer ever
/// read, leaving every drawing method a literal empty body — a style-state stub with no backing store.
/// The pixel-readback API was then deliberately left <em>absent</em> rather than stubbed, because
/// returning zeroed pixels would have turned an honest <c>TypeError</c> (which every feature detector on
/// the web reads correctly as "no canvas readback") into a false claim of support. That reasoning held
/// only while nothing rasterised; a real backing store is what retires it, and it is what this type now
/// is. See <c>docs/html5test-exceptions.md</c>.
/// </para>
/// <para>
/// <b>What is exact and what is approximate.</b> Rect fills, <c>clearRect</c>, path fills and strokes,
/// <c>globalAlpha</c> and the separable <c>globalCompositeOperation</c> blend modes rasterise through
/// <see cref="BCanvas"/> and are as exact as that rasteriser is. Text goes through
/// <see cref="BImageRenderer"/>'s glyph path — real outlines from the system font — but the pen advance
/// <see cref="MeasureTextWidth"/> reports is the renderer's own estimate rather than a shaped advance,
/// so <c>measureText</c> and the non-<c>start</c> <c>textAlign</c> values that derive from it are
/// approximate. There is no transform stack: the binding exposes no <c>translate</c>/<c>rotate</c>/
/// <c>scale</c>, so none is needed, and <see cref="BCanvas"/> is a translate+uniform-scale rasteriser
/// that could not carry a general affine anyway.
/// </para>
/// <para>
/// <b>A zero-area canvas has no bitmap.</b> <c>&lt;canvas width="0"&gt;</c> is legal and
/// <see cref="BBitmap"/> rejects a zero dimension, so <see cref="_bitmap"/> is null there and every
/// operation degrades to the answer the spec gives for an empty bitmap. The same null stands in when the
/// requested bitmap is too large to allocate.
/// </para>
/// <para>
/// <b>Not <see cref="IDisposable"/>.</b> The bitmap is a <c>byte[]</c> and <see cref="BBitmap"/>'s own
/// <c>Dispose</c> only sets a flag, so there is nothing to release deterministically; the context lives
/// as long as the canvas element that owns it and the GC reclaims both. A <c>Dispose</c> nothing calls
/// would be a footgun rather than hygiene — calling it would leave every later drawing call throwing
/// <c>ObjectDisposedException</c> on a canvas the page still holds.
/// </para>
/// </remarks>
internal sealed class CanvasRenderingContext2D
{
    /// <summary>
    /// Largest bitmap this context will allocate, in pixels (256 MiB of RGBA). A page is free to ask for
    /// a canvas far larger than it can use — <c>width</c> and <c>height</c> are unsigned longs in the IDL
    /// — and the spec's answer to a bitmap that cannot be allocated is to have no bitmap rather than to
    /// fault, so an over-large request degrades exactly as a zero-area one does.
    /// </summary>
    private const long MaxBitmapPixels = 64L * 1024 * 1024;

    private BBitmap? _bitmap;
    private readonly Stack<CanvasState> _stateStack = new();
    private readonly List<List<PointF>> _subpaths = [];

    public CanvasRenderingContext2D(int width, int height) => Allocate(width, height);

    /// <summary>Canvas width in pixels.</summary>
    public int Width { get; private set; }
    /// <summary>Canvas height in pixels.</summary>
    public int Height { get; private set; }
    /// <summary>Current fill color.</summary>
    public string FillStyle { get; set; } = "#000000";
    /// <summary>Current stroke color.</summary>
    public string StrokeStyle { get; set; } = "#000000";
    /// <summary>Current line width.</summary>
    public float LineWidth { get; set; } = 1.0f;
    /// <summary>Current font specification.</summary>
    public string Font { get; set; } = "10px sans-serif";
    /// <summary>Current text alignment.</summary>
    public string TextAlign { get; set; } = "start";
    /// <summary>Current global alpha (transparency).</summary>
    public float GlobalAlpha { get; set; } = 1.0f;
    /// <summary>Current compositing/blending operator.</summary>
    public string GlobalCompositeOperation { get; set; } = "source-over";

    /// <summary>True when this context has a bitmap to draw into and read back from.</summary>
    public bool HasBitmap => _bitmap is not null;

    /// <summary>
    /// Reacts to <c>canvas.width</c>/<c>canvas.height</c> being assigned: HTML resets the bitmap to
    /// transparent black and the context to its default state on <em>every</em> assignment, even one
    /// that does not change the value — which is what makes <c>canvas.width = canvas.width</c> the
    /// idiomatic way to clear a canvas. Doing this only on a size change would break that idiom
    /// silently, so the caller decides when it happens and this always resets.
    /// </summary>
    public void ResetSurface(int width, int height)
    {
        _bitmap?.Dispose();
        Allocate(width, height);

        _stateStack.Clear();
        _subpaths.Clear();
        FillStyle = "#000000";
        StrokeStyle = "#000000";
        LineWidth = 1.0f;
        Font = "10px sans-serif";
        TextAlign = "start";
        GlobalAlpha = 1.0f;
        GlobalCompositeOperation = "source-over";
    }

    private void Allocate(int width, int height)
    {
        Width = Math.Max(0, width);
        Height = Math.Max(0, height);
        _bitmap = Width > 0 && Height > 0 && (long)Width * Height <= MaxBitmapPixels
            ? new BBitmap(Width, Height)
            : null;
    }

    // ---- rectangles -------------------------------------------------------------------------------

    /// <summary>Fills a rectangle at the specified position and size.</summary>
    public void FillRect(float x, float y, float width, float height)
    {
        if (width == 0 || height == 0)
            return;
        Draw(canvas => canvas.FillRect(Normalize(x, y, width, height), ResolveColor(FillStyle)));
    }

    /// <summary>Strokes a rectangle outline at the specified position and size.</summary>
    public void StrokeRect(float x, float y, float width, float height) =>
        Draw(canvas => canvas.DrawRectangleStroke(
            Normalize(x, y, width, height), ResolveColor(StrokeStyle), Math.Max(LineWidth, 0f)));

    /// <summary>
    /// Clears a rectangular area, making it fully transparent. This <em>replaces</em> the pixels rather
    /// than compositing onto them, so it is written straight to the bitmap: every blending path in
    /// <see cref="BCanvas"/> would leave a fully transparent source with no effect at all.
    /// </summary>
    public void ClearRect(float x, float y, float width, float height)
    {
        if (_bitmap is null || width == 0 || height == 0)
            return;

        RectangleF rect = Normalize(x, y, width, height);
        int minX = Math.Max(0, (int)MathF.Floor(rect.Left));
        int minY = Math.Max(0, (int)MathF.Floor(rect.Top));
        int maxX = Math.Min(Width - 1, (int)MathF.Ceiling(rect.Right) - 1);
        int maxY = Math.Min(Height - 1, (int)MathF.Ceiling(rect.Bottom) - 1);

        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
                _bitmap.SetPixel(px, py, BColor.Transparent);
        }
    }

    // ---- paths ------------------------------------------------------------------------------------

    /// <summary>Begins a new drawing path.</summary>
    public void BeginPath() => _subpaths.Clear();

    /// <summary>Moves the pen to the specified point without drawing.</summary>
    public void MoveTo(float x, float y) => _subpaths.Add([new PointF(x, y)]);

    /// <summary>Draws a straight line from the current point to the specified point.</summary>
    public void LineTo(float x, float y) => CurrentSubpath().Add(new PointF(x, y));

    /// <summary>
    /// Draws an arc centered at (x, y) with the given radius and angles, flattened into line segments
    /// fine enough that the chord never departs from the true arc by more than a quarter pixel.
    /// </summary>
    public void Arc(float x, float y, float radius, float startAngle, float endAngle)
    {
        if (radius <= 0)
        {
            CurrentSubpath().Add(new PointF(x, y));
            return;
        }

        double sweep = endAngle - startAngle;
        // A canvas arc without an explicit direction runs clockwise, wrapping at most one full turn.
        if (sweep > Math.Tau)
            sweep = Math.Tau;
        else if (sweep < -Math.Tau)
            sweep = -Math.Tau;

        int steps = FlatteningSteps(radius, Math.Abs(sweep));
        List<PointF> subpath = CurrentSubpath();
        for (int i = 0; i <= steps; i++)
        {
            double angle = startAngle + (sweep * i / steps);
            subpath.Add(new PointF(
                x + (float)(radius * Math.Cos(angle)),
                y + (float)(radius * Math.Sin(angle))));
        }
    }

    /// <summary>Closes the current path by connecting the last point to the first.</summary>
    public void ClosePath()
    {
        if (_subpaths.Count == 0)
            return;
        List<PointF> subpath = _subpaths[^1];
        if (subpath.Count > 1 && subpath[0] != subpath[^1])
            subpath.Add(subpath[0]);
    }

    /// <summary>Fills the current path with the current fill style.</summary>
    public void Fill()
    {
        if (_subpaths.Count == 0)
            return;
        Draw(canvas =>
        {
            BColor color = ResolveColor(FillStyle);
            foreach (List<PointF> subpath in _subpaths)
            {
                if (subpath.Count >= 3)
                    canvas.FillPolygon([.. subpath], color);
            }
        });
    }

    /// <summary>Strokes the current path with the current stroke style.</summary>
    public void Stroke()
    {
        if (_subpaths.Count == 0 || LineWidth <= 0)
            return;
        Draw(canvas =>
        {
            BColor color = ResolveColor(StrokeStyle);
            foreach (List<PointF> subpath in _subpaths)
            {
                if (subpath.Count >= 2)
                    canvas.DrawPathStroke(subpath, color, LineWidth);
            }
        });
    }

    // ---- text -------------------------------------------------------------------------------------

    /// <summary>Fills text at the specified position, where <paramref name="y"/> is the baseline.</summary>
    public void FillText(string text, float x, float y) => DrawText(text, x, y, ResolveColor(FillStyle));

    /// <summary>
    /// Strokes text at the specified position. The glyph rasteriser fills outlines rather than stroking
    /// them, so this paints the same glyphs in the stroke colour — the shape is right and the hairline
    /// interior of an outlined glyph is not.
    /// </summary>
    public void StrokeText(string text, float x, float y) => DrawText(text, x, y, ResolveColor(StrokeStyle));

    /// <summary>
    /// Advance width of <paramref name="text"/> in the current font. This is the same per-character
    /// estimate <see cref="BImageRenderer"/> uses for its own block advance rather than a shaped
    /// advance, so it tracks the drawn text's extent only approximately.
    /// </summary>
    public double MeasureTextWidth(string text) =>
        string.IsNullOrEmpty(text) ? 0.0 : text.Length * Math.Max(1.0, FontSizePixels() * 0.62);

    // ---- pixel access -----------------------------------------------------------------------------

    /// <summary>
    /// Reads back a rectangle of straight-alpha RGBA pixels. Out-of-bounds pixels read as transparent
    /// black, as the spec requires, so the returned buffer is always <c>width * height * 4</c> bytes.
    /// </summary>
    public byte[] GetImageData(int sx, int sy, int width, int height)
    {
        var data = new byte[(long)width * height * 4];
        if (_bitmap is null)
            return data;

        for (int row = 0; row < height; row++)
        {
            int srcY = sy + row;
            if (srcY < 0 || srcY >= Height)
                continue;
            for (int column = 0; column < width; column++)
            {
                int srcX = sx + column;
                if (srcX < 0 || srcX >= Width)
                    continue;

                BColor pixel = _bitmap.GetPixel(srcX, srcY);
                int offset = ((row * width) + column) * 4;
                data[offset] = pixel.R;
                data[offset + 1] = pixel.G;
                data[offset + 2] = pixel.B;
                data[offset + 3] = pixel.A;
            }
        }

        return data;
    }

    /// <summary>
    /// Writes a block of straight-alpha RGBA pixels back into the bitmap at
    /// (<paramref name="dx"/>, <paramref name="dy"/>). The pixels <em>replace</em> what is there —
    /// putImageData is not affected by <c>globalAlpha</c>, the composite operator or the clip.
    /// </summary>
    public void PutImageData(byte[] data, int dataWidth, int dataHeight, int dx, int dy)
    {
        if (_bitmap is null)
            return;

        for (int row = 0; row < dataHeight; row++)
        {
            int destY = dy + row;
            if (destY < 0 || destY >= Height)
                continue;
            for (int column = 0; column < dataWidth; column++)
            {
                int destX = dx + column;
                if (destX < 0 || destX >= Width)
                    continue;

                int offset = ((row * dataWidth) + column) * 4;
                if (offset + 3 >= data.Length)
                    return;

                _bitmap.SetPixel(destX, destY, new BColor(
                    data[offset], data[offset + 1], data[offset + 2], data[offset + 3]));
            }
        }
    }

    /// <summary>
    /// Encodes the bitmap in <paramref name="format"/> and reports the media type actually produced,
    /// which the caller needs because it may not be the one asked for.
    /// </summary>
    public byte[] Encode(ImageEncodeFormat format, int quality) =>
        _bitmap is null ? [] : _bitmap.Encode(format, quality);

    // ---- state ------------------------------------------------------------------------------------

    /// <summary>Saves the current drawing state onto a stack.</summary>
    public void Save() => _stateStack.Push(new CanvasState
    {
        FillStyle = FillStyle,
        StrokeStyle = StrokeStyle,
        LineWidth = LineWidth,
        Font = Font,
        TextAlign = TextAlign,
        GlobalAlpha = GlobalAlpha,
        GlobalCompositeOperation = GlobalCompositeOperation,
    });

    /// <summary>Restores the most recently saved drawing state from the stack.</summary>
    public void Restore()
    {
        if (_stateStack.Count == 0)
            return;
        var state = _stateStack.Pop();
        FillStyle = state.FillStyle;
        StrokeStyle = state.StrokeStyle;
        LineWidth = state.LineWidth;
        Font = state.Font;
        TextAlign = state.TextAlign;
        GlobalAlpha = state.GlobalAlpha;
        GlobalCompositeOperation = state.GlobalCompositeOperation;
    }

    // ---- internals --------------------------------------------------------------------------------

    /// <summary>
    /// Runs one drawing operation against the bitmap, wrapped in a blend layer when
    /// <see cref="GlobalCompositeOperation"/> asks for one. The layer is per-operation because a canvas
    /// operator applies to each draw against the accumulated bitmap, not to a group of them.
    /// </summary>
    private void Draw(Action<BCanvas> operation)
    {
        if (_bitmap is null)
            return;

        using BCanvas canvas = _bitmap.OpenCanvas();
        string? blendMode = BlendModeFor(GlobalCompositeOperation);
        if (blendMode is null)
        {
            operation(canvas);
            return;
        }

        canvas.SaveBlendLayer(blendMode);
        operation(canvas);
        canvas.RestoreBlendLayer();
    }

    /// <summary>
    /// Maps a <c>globalCompositeOperation</c> to the blend mode <see cref="BCanvas"/> implements, or
    /// null for plain source-over.
    /// </summary>
    private static string? BlendModeFor(string operation) => operation?.ToLowerInvariant() switch
    {
        "multiply" or "screen" or "overlay" or "darken" or "lighten"
            or "difference" or "plus-lighter" => operation.ToLowerInvariant(),
        _ => null,
    };

    /// <summary>
    /// Whether the setter should accept this <c>globalCompositeOperation</c>. HTML requires an
    /// unrecognised value to be ignored — the state keeps its previous value — and this context treats
    /// an operator it cannot actually composite as unrecognised for the same reason the pixel APIs were
    /// absent rather than stubbed: a value that reads back as if it had been applied is a claim of
    /// support, and every canvas feature detector tests exactly this round-trip.
    /// </summary>
    public static bool IsSupportedCompositeOperation(string? operation) =>
        string.Equals(operation, "source-over", StringComparison.OrdinalIgnoreCase)
        || BlendModeFor(operation ?? string.Empty) is not null;

    private List<PointF> CurrentSubpath()
    {
        if (_subpaths.Count == 0)
            _subpaths.Add([]);
        return _subpaths[^1];
    }

    /// <summary>
    /// Segment count for flattening an arc so the chord's sagitta stays under a quarter pixel:
    /// <c>r(1 - cos(θ/2)) ≤ ¼</c>. Bounded at both ends so a hairline arc still gets a few segments and
    /// a huge one cannot produce an unbounded polygon.
    /// </summary>
    private static int FlatteningSteps(double radius, double sweep)
    {
        if (sweep <= 0)
            return 1;
        double maxAngle = 2 * Math.Acos(Math.Max(-1.0, 1.0 - (0.25 / radius)));
        int steps = maxAngle <= 0 ? int.MaxValue : (int)Math.Ceiling(sweep / maxAngle);
        return Math.Clamp(steps, 4, 4096);
    }

    /// <summary>Turns a possibly negative-extent rectangle into the positive one the rasteriser wants.</summary>
    private static RectangleF Normalize(float x, float y, float width, float height) =>
        new(
            width < 0 ? x + width : x,
            height < 0 ? y + height : y,
            Math.Abs(width),
            Math.Abs(height));

    /// <summary>
    /// Resolves a CSS colour string and folds <see cref="GlobalAlpha"/> into it. An unparseable style is
    /// the spec's no-op — the style keeps its previous value — but by the time it reaches here the
    /// setter has already accepted it, so black is the fallback the reference implementations use.
    /// </summary>
    private BColor ResolveColor(string style)
    {
        if (!CssValueParser.TryParseColor(style, out CssColor color))
            color = new CssColor(0, 0, 0);

        float alpha = color.Alpha * Math.Clamp(GlobalAlpha, 0f, 1f);
        return new BColor(color.Red, color.Green, color.Blue, (byte)Math.Clamp((int)MathF.Round(alpha), 0, 255));
    }

    /// <summary>
    /// Rasterises one text run through <see cref="BImageRenderer"/>, which owns the glyph outlines, and
    /// composites the result.
    /// </summary>
    /// <remarks>
    /// The renderer clears whatever surface it is handed, so the run cannot be drawn straight onto the
    /// canvas and goes to a scratch bitmap first. That scratch is sized to <b>the part of the run the
    /// canvas can actually show</b>, not to the run: a page may draw a megabyte of text at
    /// <c>font: 1000px</c>, and a scratch sized to that would be an allocation no machine has — while
    /// every pixel of it outside the canvas is discarded on composite anyway. The run's origin is
    /// shifted by however much of it falls off the left or top edge, so clipping the surface moves no
    /// glyph.
    /// </remarks>
    private void DrawText(string text, float x, float y, BColor color)
    {
        if (_bitmap is null || string.IsNullOrEmpty(text) || color.A == 0)
            return;

        double fontSize = FontSizePixels();
        double runWidth = MeasureTextWidth(text);

        // Where the run would land on the canvas, with a margin for glyphs that overhang their advance.
        double margin = fontSize;
        double left = x - AlignmentOffset(runWidth) - margin;
        double top = y - fontSize;
        double right = left + runWidth + (margin * 2);
        double bottom = top + (fontSize * 2);

        // Intersect with the canvas; everything outside it is discarded on composite regardless.
        double visibleLeft = Math.Max(0, left);
        double visibleTop = Math.Max(0, top);
        int scratchWidth = (int)Math.Ceiling(Math.Min(Width, right) - visibleLeft);
        int scratchHeight = (int)Math.Ceiling(Math.Min(Height, bottom) - visibleTop);
        if (scratchWidth <= 0 || scratchHeight <= 0)
            return;

        // BImageRenderer places a run's baseline at origin.Y + fontSize * 0.8, so an origin this far
        // above the baseline puts it where the caller asked. Both coordinates are relative to the
        // scratch, which starts at the visible corner rather than at the run's own.
        var origin = new BPoint(
            left + margin - visibleLeft,
            (y - (fontSize * 0.8)) - visibleTop);

        var renderList = new BRenderList(1);
        renderList.DrawText(
            new BTextRun(text, new BFontStyle(FontFamily(), fontSize, FontWeight()), color),
            origin);

        using var renderer = new BImageRenderer();
        using BBitmap scratch = renderer.RenderToImage(
            renderList,
            new BSurfaceDescriptor(new BSize(scratchWidth, scratchHeight), 1.0),
            new BFrameContext(BColor.Transparent));

        var destination = new RectangleF((float)visibleLeft, (float)visibleTop, scratchWidth, scratchHeight);
        var source = new RectangleF(0, 0, scratchWidth, scratchHeight);
        Draw(canvas => canvas.DrawBitmap(scratch, destination, source));
    }

    /// <summary>
    /// Leftward shift for the current <c>textAlign</c>. <c>start</c>/<c>end</c> resolve as for
    /// left-to-right text: this context has no direction of its own to consult.
    /// </summary>
    private float AlignmentOffset(double runWidth) => TextAlign?.ToLowerInvariant() switch
    {
        "center" => (float)(runWidth / 2),
        "right" or "end" => (float)runWidth,
        _ => 0f,
    };

    /// <summary>
    /// Pulls the pixel size out of the CSS shorthand held in <c>font</c>. Only <c>px</c> is read: the
    /// context has no element to resolve <c>em</c>, percentages or keywords against.
    /// </summary>
    private double FontSizePixels()
    {
        foreach (string token in (Font ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!token.EndsWith("px", StringComparison.OrdinalIgnoreCase))
                continue;
            if (double.TryParse(
                    token[..^2],
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double size) && size > 0)
                return size;
        }

        return 10.0;
    }

    private string FontFamily()
    {
        string font = Font ?? string.Empty;
        int lastSize = font.LastIndexOf("px", StringComparison.OrdinalIgnoreCase);
        if (lastSize < 0 || lastSize + 2 >= font.Length)
            return "sans-serif";

        string family = font[(lastSize + 2)..].Trim().Split(',')[0].Trim().Trim('"', '\'');
        return family.Length == 0 ? "sans-serif" : family;
    }

    private BFontWeight FontWeight() =>
        (Font ?? string.Empty).Contains("bold", StringComparison.OrdinalIgnoreCase)
            ? BFontWeight.Bold
            : BFontWeight.Normal;

    private sealed class CanvasState
    {
        public string FillStyle { get; init; } = string.Empty;
        public string StrokeStyle { get; init; } = string.Empty;
        public float LineWidth { get; init; }
        public string Font { get; init; } = string.Empty;
        public string TextAlign { get; init; } = string.Empty;
        public float GlobalAlpha { get; init; }
        public string GlobalCompositeOperation { get; init; } = string.Empty;
    }
}
