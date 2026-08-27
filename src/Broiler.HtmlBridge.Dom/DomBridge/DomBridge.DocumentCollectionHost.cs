using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IDocumentCollectionHost implementation for the DocumentCollectionBinding feature module
// (Phase 3): the bridge exposes the element list, JS-wrapper factory and stylesheet-object builder
// via explicit interface members, so the module never reaches an arbitrary bridge private field and
// the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IDocumentCollectionHost
{
    JSObject Dom.Features.IDocumentCollectionHost.ToJSObject(DomNode node) => ToJSObject(node);

    IReadOnlyList<DomElement> Dom.Features.IDocumentCollectionHost.Elements => Elements;

    int Dom.Features.IDocumentCollectionHost.CurrentScriptIndex => CurrentScriptIndex;

    JSObject Dom.Features.IDocumentCollectionHost.BuildStyleSheetObject(DomElement styleElement)
        => BuildStyleSheetObject(styleElement);

    bool Dom.Features.IDocumentCollectionHost.HasAssociatedStyleSheet(DomElement element)
        => HasAssociatedStyleSheet(element);
}
