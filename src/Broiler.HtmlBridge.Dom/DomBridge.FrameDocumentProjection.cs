using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// A scripted <c>src</c> frame's live document, carried to the renderer.
/// <para>
/// A nested browsing context reaches the renderer as markup on its container element: the renderer
/// has no bridge to ask. An <c>&lt;iframe srcdoc&gt;</c> already round-trips that way — the srcdoc
/// attribute is re-serialized from the live sub-document — but a frame loaded from <c>src</c> had no
/// such carrier, so <c>FragmentTreeBuilder.TryLoadEmbeddedDocument</c> re-read the resource
/// <em>from disk</em> and rendered the frame as the file, discarding everything the sub-document's
/// own scripts (or a parent reaching in through <c>frames[0]</c>) had done to it.
/// </para>
/// <para>
/// WPT <c>css-view-transitions/transition-in-empty-iframe</c> is exactly that: the parent calls
/// <c>frames[0].window.startTransition()</c>, the child's update callback unhides a limegreen box,
/// and the frame still painted the file's hidden box — an empty frame (issue #1552). The live
/// document is stamped on the container instead, and only when it has actually diverged from the
/// resource as parsed, so a frame nobody scripted still renders straight from its file.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>The render-time carrier for a <c>src</c> frame's live document — the counterpart of
    /// <c>srcdoc</c>, read by <c>FragmentTreeBuilder.TryLoadEmbeddedDocument</c>.</summary>
    private const string FrameDocumentAttr = "data-broiler-frame-document";

    /// <summary>The URL the frame's document was loaded from, so relative references inside it
    /// resolve against the resource rather than against the containing page.</summary>
    private const string FrameDocumentBaseAttr = "data-broiler-frame-base";

    /// <summary>Each sub-document as its resource parsed — the "before any script touched it"
    /// serialization that says whether the live document has diverged.</summary>
    private readonly Dictionary<DomDocument, string> _subDocumentSourceMarkup = [];

    /// <summary>The doctype each sub-document's resource declared, so a stamped document keeps the
    /// rendering mode its file had. Serialization emits the document element alone.</summary>
    private readonly Dictionary<DomDocument, string> _subDocumentDoctype = [];

    /// <summary>Records how <paramref name="document"/> looked as parsed from
    /// <paramref name="html"/>, before any script ran against it.</summary>
    private void RecordSubDocumentSourceMarkup(DomDocument document, string html)
    {
        if (SerializeSubDocumentChildren(document) is { } markup)
            _subDocumentSourceMarkup[document] = markup;
        _subDocumentDoctype[document] = HasHtmlDoctype(html) ? "<!DOCTYPE html>" : string.Empty;
    }

    /// <summary>Whether the resource opens with a doctype, ignoring leading whitespace and any
    /// comments before it.</summary>
    private static bool HasHtmlDoctype(string html)
    {
        var index = 0;
        while (index < html.Length)
        {
            while (index < html.Length && char.IsWhiteSpace(html[index]))
                index++;

            if (index >= html.Length)
                return false;

            if (!html.AsSpan(index).StartsWith("<!--", StringComparison.Ordinal))
                break;

            var end = html.IndexOf("-->", index, StringComparison.Ordinal);
            if (end < 0)
                return false;
            index = end + 3;
        }

        return html.AsSpan(index).StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stamps the live document of every <c>src</c>-loaded nested browsing context in the projection
    /// that has diverged from its resource, so the renderer paints what the frame became instead of
    /// what its file says.
    /// </summary>
    private void ProjectScriptedFrameDocuments(DomElement element)
    {
        foreach (var child in ChildElements(element).ToList())
            ProjectScriptedFrameDocuments(child);

        // A srcdoc frame already carries its live document in the attribute it was authored with.
        if (!IsNestedBrowsingContextContainer(element.TagName?.ToLowerInvariant()) ||
            HasAttr(element, "srcdoc"))
        {
            return;
        }

        var source = ResolveRenderSource(element);
        if (GetContentDocument(source) is not { } subDocumentRoot ||
            RenderedSubDocumentMarkup(subDocumentRoot) is not { Length: > 0 } markup)
        {
            return;
        }

        // The stamp stands in for the renderer's own loading of the frame's resource, so it applies
        // only where there is a resource to stand in for — a frame with no recorded source markup
        // (about:blank, an empty <iframe>) has nothing being re-read and is left alone. Scripting
        // content into such a frame still renders empty; that is a separate gap.
        //
        // And when the document still matches its resource, the renderer's file path already paints
        // it correctly: keeping every untouched frame off the serialize-and-reparse round trip.
        if (!_subDocumentSourceMarkup.TryGetValue(subDocumentRoot, out var sourceMarkup) ||
            string.Equals(sourceMarkup, markup, StringComparison.Ordinal))
        {
            return;
        }

        SetAttr(element, FrameDocumentAttr,
            _subDocumentDoctype.GetValueOrDefault(subDocumentRoot, string.Empty) + markup);

        if (GetSubDocumentBaseUrl(source) is { Length: > 0 } baseUrl)
            SetAttr(element, FrameDocumentBaseAttr, baseUrl);
    }
}
