using System.Text.RegularExpressions;
using Broiler.CSS;
using Broiler.CSS.Dom;
using Broiler.Dom;
using Broiler.Dom.Html;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static readonly System.Text.RegularExpressions.Regex DocTypePattern = DocTypePatternRegex();

    /// <summary>
    /// Parses a CSS inline style string (e.g. <c>"color: red; font-size: 12px"</c>)
    /// into a property→value dictionary. Implements CSS error recovery: when the
    /// same property is declared multiple times, invalid values are discarded so
    /// the last <em>valid</em> value wins (per CSS 2.1 §4.2 / CSS Syntax §5).
    /// </summary>
    /// <param name="reportDrops">
    /// When <c>true</c>, declarations rejected by
    /// <see cref="CSS.Dom.CssDeclarationValidator.IsAcceptableDeclarationValue"/>
    /// are surfaced through
    /// <see cref="CSS.Dom.CssEngineDiagnostics.DeclarationRejected"/>
    /// (diagnostic #1b). The bridge rewrites the serialized <c>style</c> attribute
    /// from the survivors of this filter (see <c>PrepareCanonicalDocumentForRendering</c>),
    /// so a dropped inline declaration vanishes before the renderer's own style engine
    /// can report it — this is the only place such drops are observable. Set it only at
    /// inline-style <em>ingestion</em> sites that write <c>InlineStyle(element)</c> (so the drop
    /// reaches the rendered output); leave it off for query/bookkeeping re-parses and for
    /// stylesheet-rule / descriptor parsing (cascade drops the style engine already reports).
    /// </param>
    internal static Dictionary<string, string> ParseStyle(string styleValue, bool reportDrops = false)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var declarations = new CSS.CssParser().ParseDeclarations(styleValue);
        foreach (var declaration in declarations.Declarations)
        {
            var prop = declaration.Name;
            // Validate the importance-stripped value against the shared CSS.Dom
            // declaration table (the same closed-keyword error-recovery the cascade
            // uses), then re-attach the "!important" suffix the bridge-owned
            // declaration map carries as part of the string value.
            var rawValue = declaration.Value.Text;

            if (CssDeclarationValidator.IsAcceptableDeclarationValue(prop, rawValue))
            {
                var val = declaration.Important ? rawValue + " !important" : rawValue;
                result[prop] = val;
                // Map vendor-prefixed property to unprefixed equivalent (TODO-G9)
                var unprefixed = CssPropertyNames.StripVendorPrefix(prop);
                if (unprefixed != prop && !result.ContainsKey(unprefixed))
                    result[unprefixed] = val;
            }
            else if (reportDrops)
            {
                // Report the raw value (without any synthetic " !important" suffix) so
                // inline drops aggregate identically to the engine's stylesheet drops.
                CssEngineDiagnostics.DeclarationRejected?.Invoke(prop, rawValue);
            }
        }
        return result;
    }

    /// <summary>
    /// Whether <paramref name="value"/> is an acceptable declared value for
    /// <paramref name="property"/> per the shared <see cref="CSS.Dom.CssDeclarationValidator"/> —
    /// the same closed-keyword error-recovery the inline-style <em>attribute</em> path
    /// (<see cref="ParseStyle"/>) applies. A live <c>CSSStyleDeclaration</c> per-property setter
    /// (<c>el.style.color = …</c>, <c>setProperty(…)</c>, <c>cssFloat = …</c>) must <em>reject</em>
    /// an invalid value rather than store it, matching the attribute path (where
    /// <c>el.style = "color: bogus"</c> already drops the declaration) and CSSOM error handling.
    /// The value may carry a trailing <c>!important</c>, which is stripped before validation;
    /// unknown and custom (<c>--*</c>) properties are always accepted (the validator's default).
    /// </summary>
    internal static bool IsAcceptableInlineValue(string property, string value) =>
        CssDeclarationValidator.IsAcceptableDeclarationValue(property, CssPriority.Strip(value));

    [GeneratedRegex(@"<!DOCTYPE\s+(\w+)(?:\s+PUBLIC\s+""([^""]*)""(?:\s+""([^""]*)"")?)?\s*>", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial System.Text.RegularExpressions.Regex DocTypePatternRegex();
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Parses a full HTML document via the shared <see cref="HtmlDocumentParser"/> and returns the
    /// canonical <c>&lt;html&gt;</c> root, the parsed <c>&lt;!DOCTYPE&gt;</c> node (or <c>null</c>),
    /// the non-structural node registration list, and the title. Replaces the retired
    /// <c>HtmlTreeBuilder.Build</c>.
    /// </summary>
    internal static (DomElement DocumentElement, DomDocumentType? DocumentType, List<DomNode> AllElements, string Title) BuildDocumentTree(string html)
    {
        var parsed = HtmlDocumentParser.ParseDocument(html);
        var root = parsed.Document.DocumentElement ??
            throw new InvalidOperationException("The shared HTML parser did not produce a document element.");

        var allElements = new List<DomNode>();
        AppendParsedTreeNodes(root, structural: true, allElements);
        return (root, parsed.Document.DocumentType, allElements, parsed.Title);
    }

    /// <summary>
    /// Collects the registration set in document order, excluding the structural scaffold
    /// (<c>&lt;html&gt;</c> and its direct <c>&lt;head&gt;</c>/<c>&lt;body&gt;</c> children) exactly as the
    /// retired <c>HtmlTreeBuilder.ConvertNode</c>'s <c>structural</c> flag did.
    /// </summary>
    internal static void AppendParsedTreeNodes(DomNode source, bool structural, List<DomNode> allElements)
    {
        if (!structural)
            allElements.Add(source);

        foreach (var child in source.ChildNodes)
        {
            var childIsStructural = structural &&
                child is DomElement childElement &&
                childElement.LocalName is "head" or "body";
            
            AppendParsedTreeNodes(child, childIsStructural, allElements);
        }
    }
}

public static partial class DomBridgeUtils
{
    /// <summary>Whether <paramref name="element"/> is an HTML <c>&lt;template&gt;</c>.</summary>
    internal static bool IsTemplateElement(DomElement element) =>
        string.Equals(element.TagName, "template", StringComparison.OrdinalIgnoreCase);
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Returns the top-level nodes overlapping the given range boundaries, for the range
    /// client-rect geometry. For a single container this is the children between the offsets;
    /// across containers it is the document-order nodes strictly between start and end, keeping
    /// only those not already covered by an included ancestor. This is a client-rect geometry
    /// heuristic — it includes partially-overlapping elements, unlike the spec-strict
    /// <c>DomRange.IsContained</c> set — so it stays bridge-owned rather than promoting to
    /// canonical Broiler.Dom. It reuses canonical <see cref="DomNode.InclusiveDescendants"/> for
    /// the document-order walk.
    /// </summary>
    internal static List<DomNode> GetNodesInRange(DomNode startContainer, int startOffset, DomNode endContainer, int endOffset)
    {
        var result = new List<DomNode>();
        if (ReferenceEquals(startContainer, endContainer))
        {
            // Same container — return children between offsets
            for (var i = startOffset; i < Math.Min(endOffset, startContainer.ChildNodes.Count); i++)
                result.Add(ChildAt(startContainer, i));
            return result;
        }

        // Different containers — collect nodes between start and end
        var ancestor = startContainer.CommonAncestorWith(endContainer);
        if (ancestor == null) return result;

        var allNodes = ancestor.InclusiveDescendants().ToList();
        var startIdx = allNodes.IndexOf(startContainer);
        var endIdx = allNodes.IndexOf(endContainer);
        if (startIdx < 0 || endIdx < 0) return result;

        for (var i = startIdx + 1; i < endIdx; i++)
        {
            var node = allNodes[i];
            // Only include top-level nodes (not descendants of already-included nodes)
            var isDescendantOfIncluded = result.Any(r => node.IsDescendantOf(r));
            if (!isDescendantOfIncluded)
                result.Add(node);
        }
        return result;
    }

    internal static void UpdateCharacterData(DomNode target, string? newValue) => SetBridgeText(target, newValue ?? string.Empty);
}

public static partial class DomBridgeUtils
{
    // -----------------------------------------------------------------
    // RF-BRIDGE-1c Phase C: string-keyed attribute access over canonical
    // Broiler.Dom attributes, replacing the removed Broiler.Dom.DomElement.Attributes
    // (LegacyAttributeDictionary) facade. Each helper mirrors the legacy
    // dictionary's semantics exactly — a case-insensitive scan by qualified
    // name over the canonical (namespace-keyed) attribute set — so the
    // migration is behaviour-preserving (same O(n) scan the shim did).
    // -----------------------------------------------------------------

    /// <summary>Legacy <c>Attributes.TryGetValue</c>: case-insensitive lookup by
    /// qualified name; <paramref name="value"/> is <c>""</c> when absent.</summary>
    internal static bool TryGetAttribute(DomElement element, string qualifiedName, out string value)
    {
        foreach (var attribute in element.Attributes.Values)
        {
            if (string.Equals(attribute.QualifiedName, qualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                value = attribute.Value;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Legacy <c>Attributes.GetValueOrDefault</c> / null-returning indexer get.</summary>
    internal static string? GetAttr(DomElement element, string qualifiedName) =>
        TryGetAttribute(element, qualifiedName, out var value) ? value : null;

    /// <summary>Legacy <c>Attributes.ContainsKey</c>.</summary>
    internal static bool HasAttr(DomElement element, string qualifiedName) =>
        TryGetAttribute(element, qualifiedName, out _);

    /// <summary>Legacy string-keyed <c>Attributes[name] = value</c> setter: updates an
    /// existing attribute in place (preserving its namespace) or creates a no-namespace one.</summary>
    internal static void SetAttr(DomElement element, string qualifiedName, string value)
    {
        DomAttribute? existing = null;
        foreach (var attribute in element.Attributes.Values)
        {
            if (string.Equals(attribute.QualifiedName, qualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                existing = attribute;
                break;
            }
        }

        if (existing is { } found)
            element.SetAttributeNS(found.NamespaceUri, found.QualifiedName, value);
        else
            element.SetAttribute(qualifiedName, value);
    }

    /// <summary>Legacy <c>Attributes.Remove</c>: removes the attribute matched by qualified name.</summary>
    internal static bool RemoveAttr(DomElement element, string qualifiedName)
    {
        DomAttribute? existing = null;
        foreach (var attribute in element.Attributes.Values)
        {
            if (string.Equals(attribute.QualifiedName, qualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                existing = attribute;
                break;
            }
        }

        return existing is { } found && element.RemoveAttributeNS(found.NamespaceUri, found.LocalName);
    }

    /// <summary>
    /// RF-BRIDGE-1c Phase C2: canonical replacement for the removed
    /// <c>Broiler.Dom.DomElement.NsAttrMap</c> shadow map. Looks up the attribute identified by
    /// (<paramref name="namespaceUri"/>, <paramref name="localName"/>) directly in the
    /// canonical namespace-keyed attribute set and yields its qualified (possibly
    /// prefixed) name plus value — the exact pair <c>NsAttrMap</c> used to carry. The
    /// namespace is normalized (empty string ≡ null) to match canonical attribute keying,
    /// the same normalization <c>SetAttributeNS</c>/<c>GetAttributeNS</c> apply.
    /// </summary>
    internal static bool TryGetNsAttribute(DomElement element, string? namespaceUri, string localName, out string qualifiedName, out string value)
    {
        var ns = string.IsNullOrEmpty(namespaceUri) ? null : namespaceUri;
        if (element.Attributes.TryGetValue((ns, localName), out var attribute))
        {
            qualifiedName = attribute.QualifiedName;
            value = attribute.Value;
            return true;
        }

        qualifiedName = string.Empty;
        value = string.Empty;
        return false;
    }

    /// <summary>Legacy <c>Attributes.Keys</c>: the qualified names of the element's attributes.</summary>
    internal static IEnumerable<string> AttributeNames(DomElement element) =>
        element.Attributes.Values.Select(static attribute => attribute.QualifiedName);

    /// <summary>Collects all Broiler.Dom.DomElement nodes in a sub-tree for tracking.</summary>
    private static void CollectSubDocElements(DomElement root, List<DomElement> list)
    {
        list.Add(root);
        foreach (var child in ChildElements(root))
            CollectSubDocElements(child, list);
    }

}
