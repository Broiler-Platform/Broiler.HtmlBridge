namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>The render-time carrier for a <c>src</c> frame's live document — the counterpart of
    /// <c>srcdoc</c>, read by <c>FragmentTreeBuilder.TryLoadEmbeddedDocument</c>.</summary>
    internal const string FrameDocumentAttr = "data-broiler-frame-document";

    /// <summary>The URL the frame's document was loaded from, so relative references inside it
    /// resolve against the resource rather than against the containing page.</summary>
    internal const string FrameDocumentBaseAttr = "data-broiler-frame-base";

    /// <summary>Whether the resource opens with a doctype, ignoring leading whitespace and any
    /// comments before it.</summary>
    internal static bool HasHtmlDoctype(string html)
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
}
