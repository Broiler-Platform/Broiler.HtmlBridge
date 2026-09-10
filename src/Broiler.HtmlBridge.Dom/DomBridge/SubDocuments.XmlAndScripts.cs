using System.Text;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.Dom;
using System.Xml.Linq;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>SubDocuments.cs</c> (Phase 3 ratchet, 2026-07-17) to keep it
/// under the 750-line guard: XML/XHTML/SVG sub-document construction and sub-document script
/// execution. Builds a canonical <see cref="DomDocument"/> tree from XML content
/// (<see cref="BuildSubDocumentFromXml"/> / <see cref="BuildDomElementFromXElement"/>), and — for
/// correctly-namespaced XHTML — collects and runs embedded <c>&lt;script&gt;</c> content in the
/// main JS context. Pure partial-class relocation — no signature, accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Builds a sub-document tree from XML/SVG/XHTML content using an XML parser.
    /// For XHTML with valid namespace, also executes embedded scripts.
    /// XML well-formedness errors result in an empty document.
    /// </summary>
    private DomDocument BuildSubDocumentFromXml(
        string xmlContent,
        string contentType,
        DomElement containerElement,
        ContentSecurityPolicy? deliveredPolicy = null)
    {
        var document = CreateBrowsingContextDocument();

        try
        {
            // Strip XML processing instructions before parsing (XDocument doesn't need them)
            var cleanXml = xmlContent;
            while (cleanXml.TrimStart().StartsWith("<?xml-stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                var piEnd = cleanXml.IndexOf("?>", StringComparison.Ordinal);
                if (piEnd >= 0) cleanXml = cleanXml[(piEnd + 2)..].TrimStart();
                else break;
            }

            var xdoc = XDocument.Parse(cleanXml);
            if (xdoc.Root == null)
            {
                LinkContentDocument(containerElement, document);
                return document;
            }

            // Check XHTML namespace validity
            var rootNs = xdoc.Root.Name.NamespaceName;
            var isXhtml = string.Equals(contentType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase);
            var hasCorrectXhtmlNs = string.Equals(rootNs, "http://www.w3.org/1999/xhtml", StringComparison.Ordinal);

            if (isXhtml && !hasCorrectXhtmlNs)
            {
                // Wrong XHTML namespace — create empty doc, don't execute scripts. It links
                // the container to its own document.
                return BuildEmptySubDocument(containerElement);
            }

            // Build DOM tree from XML
            var rootEl = BuildDomElementFromXElement(xdoc.Root);
            document.AppendChild(rootEl);

            LinkContentDocument(containerElement, document);

            // Execute scripts in XHTML documents with correct namespace
            if (isXhtml && hasCorrectXhtmlNs)
            {
                ExecuteSubDocumentScripts(
                    rootEl,
                    new ContentSecurityPolicySet(deliveredPolicy, ContentSecurityPolicy.FromHtml(xmlContent)));
            }
        }
        catch (System.Xml.XmlException)
        {
            // XML well-formedness error — return empty document, don't execute scripts
            LinkContentDocument(containerElement, document);
        }

        return document;
    }

    /// <summary>
    /// Recursively builds a Broiler.Dom.DomElement tree from an XElement.
    /// </summary>
    private DomElement BuildDomElementFromXElement(XElement xe)
    {
        var tagName = xe.Name.LocalName.ToLowerInvariant();
        var el = CreateBridgeElement(tagName);

        foreach (var attr in xe.Attributes())
        {
            if (!attr.IsNamespaceDeclaration)
                SetAttr(el, attr.Name.LocalName, attr.Value);
        }

        foreach (var child in xe.Nodes())
        {
            if (child is XElement childXe)
            {
                var childEl = BuildDomElementFromXElement(childXe);
                SetParent(childEl, el);
                el.AppendChild(childEl);
            }
            else if (child is XText childText)
            {
                var textNode = CreateBridgeTextNode(childText.Value);
                SetParent(textNode, el);
                el.AppendChild(textNode);
            }
        }

        return el;
    }

    /// <summary>
    /// Finds and executes script elements within a sub-document tree.
    /// Scripts call parent.notify() etc. in the main JS context.
    /// </summary>
    /// <param name="policies">
    /// Every Content-Security-Policy governing this sub-document: whatever its own markup declares,
    /// plus the one delivered to it — its embedder's when the frame has a local scheme and inherits
    /// it, or its response header's when it came off the network. Each script runs only if all of
    /// them admit it.
    /// </param>
    /// <remarks>
    /// <b>This path consulted no policy at all before, which is a different failure from consulting
    /// one that turned out to be null.</b> The HTML path at least asked, and got <c>null</c> for a
    /// frame that declared nothing; here nothing was ever looked for, so an XHTML frame ran its
    /// scripts under any policy whatever. <c>SubDocumentContentSecurityPolicyTests</c> pins it, with
    /// the control that says the same document still runs when nothing forbids it — without that
    /// control the test would pass on a path where scripts never run.
    /// </remarks>
    private void ExecuteSubDocumentScripts(DomElement docRoot, ContentSecurityPolicySet policies = default)
    {
        if (_realm is null) return;

        var scripts = new List<string>();
        CollectScriptContent(docRoot, scripts);

        foreach (var scriptCode in scripts)
        {
            // An XML sub-document's scripts are inline by construction -- CollectScriptContent takes
            // an element's text and never a src -- so the inline directive is the one that decides.
            if (!policies.AllowsInlineScript(scriptText: scriptCode))
                continue;

            try
            {
                _jsContext.Eval(scriptCode);
            }
            catch (Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.ExecuteSubDocumentScripts",
                    $"Sub-document script error: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Recursively collects text content from script elements.
    /// </summary>
    private static void CollectScriptContent(DomElement element, List<string> scripts)
    {
        if (string.Equals(element.TagName, "script", StringComparison.OrdinalIgnoreCase))
        {
            var text = GetTextContentRecursive(element);
            if (!string.IsNullOrWhiteSpace(text))
                scripts.Add(text);
            return;
        }

        foreach (var child in ChildElements(element))
            CollectScriptContent(child, scripts);
    }

    /// <summary>
    /// Gets the concatenated text content of an element and all its descendants.
    /// </summary>
    private static string GetTextContentRecursive(DomElement element)
    {
        // RF-BRIDGE-1c Phase F (F3c part 2d): aggregate descendant text over raw ChildNodes.
        var sb = new StringBuilder();
        CollectTextContent(element, sb);
        return sb.ToString();
    }
}
