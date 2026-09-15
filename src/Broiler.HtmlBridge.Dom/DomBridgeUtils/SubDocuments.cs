using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static DomElement? FindBodyElement(DomElement documentElement) =>
        ChildElements(documentElement).FirstOrDefault(c =>
            !IsText(c) &&
            string.Equals(c.TagName, "body", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reports a sub-document module root that failed after the call that started it had returned.
    /// </summary>
    /// <remarks>
    /// The root above is not awaited, so nothing else observes its exception; without this the module
    /// would simply appear not to have run, which is the one outcome that looks identical to the
    /// documented limitation and so hides a real failure inside it.
    /// </remarks>
    internal static void LogSubDocumentModuleFailure(System.Threading.Tasks.Task run, string key) =>
        run.ContinueWith(
            completed => RenderLogger.LogWarning(
                LogCategory.JavaScript,
                "DomBridge.ExecuteSubDocumentScripts",
                $"Sub-document module root {key} error: " +
                    $"{completed.Exception?.GetBaseException().Message}",
                completed.Exception),
            System.Threading.CancellationToken.None,
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted
                | System.Threading.Tasks.TaskContinuationOptions.ExecuteSynchronously,
            System.Threading.Tasks.TaskScheduler.Default);

    /// <summary>
    /// Returns true if the content type indicates XML-family content
    /// (application/xml, text/xml, image/svg+xml, application/xhtml+xml).
    /// </summary>
    /// <summary>
    /// Whether a sub-resource URL names a LOCAL SCHEME — one whose document has no response of its
    /// own, and therefore inherits its embedder's Content-Security-Policy.
    /// </summary>
    /// <remarks>
    /// An absent or blank <c>src</c> is <c>about:blank</c>, which is local, so it answers
    /// <see langword="true"/> rather than being treated as a network document that happens to have
    /// fetched nothing.
    /// </remarks>
    internal static bool IsLocalSchemeSubResource(string? resourceUrl) =>
        string.IsNullOrWhiteSpace(resourceUrl) ||
        resourceUrl.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
        resourceUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
        resourceUrl.StartsWith("blob:", StringComparison.OrdinalIgnoreCase);

    internal static bool IsXmlContentType(string contentType) =>
        string.Equals(contentType, "application/xml", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(contentType, "text/xml", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(contentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(contentType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the tag names a nested browsing context container whose resource this bridge loads
    /// as a sub-document: <c>&lt;iframe&gt;</c>, <c>&lt;object&gt;</c> or <c>&lt;frame&gt;</c>.
    /// <para>
    /// <c>&lt;frame&gt;</c> is here because a frameset's cells are nested browsing contexts exactly
    /// like an iframe's — the renderer already lays out the frameset grid
    /// (<c>DomParser.LayoutFramesetChildren</c>) and projects a container's content document as a
    /// sub-viewport, but nothing ever loaded a <c>&lt;frame src&gt;</c>, so every frameset rendered
    /// as empty cells (WPT resource-timing/initiator-type/frameset).
    /// </para>
    /// </summary>
    internal static bool IsNestedBrowsingContextContainer(string? tag) =>
        tag is "iframe" or "object" or "frame";

    /// <summary>
    /// Gets the resource URL for a container element (iframe/frame src, or object data).
    /// </summary>
    internal static string GetSubResourceUrl(DomElement containerElement)
    {
        var tag = containerElement.TagName?.ToLowerInvariant();
        if (tag == "iframe" || tag == "frame")
            return TryGetAttribute(containerElement, "src", out var src) ? src : string.Empty;
        if (tag == "object")
            return TryGetAttribute(containerElement, "data", out var data) ? data : string.Empty;
        return string.Empty;
    }

    /// <summary>
    /// The Content-Security-Policy a response delivered, or <see langword="null"/> when it carried
    /// none.
    /// </summary>
    /// <remarks>
    /// <b>Only the first policy is honoured, and a response may deliver several.</b> CSP is a list:
    /// every policy delivered is enforced, and each can only narrow the others. This bridge's
    /// <see cref="ContentSecurityPolicy"/> parses one policy, and
    /// <see cref="ContentSecurityPolicySet"/> holds a document's two sources rather than an
    /// unbounded list, so a second header is dropped. Dropping one can only make this MORE
    /// permissive than the response asked for, which is the wrong direction; it is recorded here
    /// because a reader who assumes otherwise would be building on it. The report-only header is
    /// deliberately not read: it enforces nothing.
    /// </remarks>
    internal static ContentSecurityPolicy? PolicyFromResponse(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Content-Security-Policy", out var values))
            return null;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var policy = new ContentSecurityPolicy();
            policy.Parse(value);
            return policy;
        }

        return null;
    }

    /// <summary>Sentinel content type indicating a network/file fetch failure (404, connection refused, etc.).</summary>
    internal const string FetchFailedContentType = "__fetch_failed__";

    /// <summary>
    /// Detects the MIME type of text content based on its initial bytes/structure.
    /// Used for files without a recognized extension (e.g. xhtml.1, xhtml.2).
    /// </summary>
    internal static string DetectContentTypeFromContent(string content, string filename)
    {
        if (string.IsNullOrEmpty(content))
            return "text/plain";

        var trimmed = content.TrimStart();

        // SVG detection
        if (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<svg", StringComparison.OrdinalIgnoreCase)))
            return "image/svg+xml";

        // XHTML detection (has xmlns on root html element)
        if (trimmed.Contains("xmlns=\"http://www.w3.org/1999/xhtml", StringComparison.OrdinalIgnoreCase))
            return "application/xhtml+xml";

        // Generic XML detection
        if (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.StartsWith('<') && !trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) &&
             !trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)))
            return "application/xml";

        // HTML detection
        if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            return "text/html";

        return "text/plain";
    }
}
