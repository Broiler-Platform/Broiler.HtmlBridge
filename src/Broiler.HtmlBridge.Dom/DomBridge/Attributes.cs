using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
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
