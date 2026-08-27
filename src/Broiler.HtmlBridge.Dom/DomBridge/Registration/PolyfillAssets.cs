using System.IO;
using System.Text;

namespace Broiler.HtmlBridge;

/// <summary>
/// Loads the embedded polyfill JavaScript assets (Phase 3 work item 6 — the content-rendering polyfills are
/// versioned <c>.js</c> resources embedded in <c>Broiler.HtmlBridge.Dom</c> rather than inline C# string
/// literals). Each asset is read from the assembly manifest once and cached for the process.
/// </summary>
internal static class PolyfillAssets
{
    private const string ContentRenderingResource =
        "Broiler.HtmlBridge.Polyfills.content-rendering-polyfills.js";

    private const string StreamsResource =
        "Broiler.HtmlBridge.Polyfills.streams-and-file-reader.js";

    private static string? _contentRendering;

    private static string? _streams;

    /// <summary>
    /// The content-rendering polyfill bundle: <c>Image</c>, <c>IntersectionObserver</c>,
    /// <c>ResizeObserver</c>, <c>TextEncoder</c>/<c>TextDecoder</c>, <c>URL</c>/<c>URLSearchParams</c> and
    /// <c>AbortController</c>. Evaluated once per document into the browsing-context global.
    /// </summary>
    public static string ContentRendering => _contentRendering ??= Load(ContentRenderingResource);

    /// <summary>
    /// <c>ReadableStream</c> (with its default reader and controller), <c>ProgressEvent</c> and
    /// <c>FileReader</c>. JavaScript rather than host functions for the same reason the
    /// specification is written that way — the queue, the pending read requests and the pull
    /// back-pressure are a state machine over promises.
    /// </summary>
    public static string Streams => _streams ??= Load(StreamsResource);

    private static string Load(string resourceName)
    {
        var assembly = typeof(PolyfillAssets).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded polyfill asset not found: {resourceName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
