using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    // Phase 4 item 1 (P4.4a): docRoot may be a legacy #subdoc-root element OR a canonical
    // DomDocument browsing-context root; ChildElements works over both. Returns the documentElement,
    // or — for an element root with none — the root itself (the prior `?? docRoot` fallback). A
    // canonical DomDocument with no documentElement yields null (per DOM; e.g. createDocument with an
    // empty qualifiedName), so callers must null-check.
    internal static DomElement? GetDocumentElement(DomNode docRoot) =>
        ChildElements(docRoot).FirstOrDefault(c => !IsText(c) && !c.TagName.StartsWith('#')) ?? docRoot as DomElement;
}
