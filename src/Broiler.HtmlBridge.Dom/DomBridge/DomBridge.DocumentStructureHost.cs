using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IDocumentStructureHost implementation for the DocumentStructureBinding feature module
// (Phase 3): the bridge exposes the document root, the JS-wrapper factory and the document title via
// explicit interface members, so the module never reaches an arbitrary bridge private field and the
// public surface is unchanged.
//
// The contract is spelled in JSEAL now, and this file is the seam that half of the migration leaves
// behind: ToJSObject is the unmigrated bridge's wrapper factory and hands back an engine object, so
// JsInterop mints the handle over it. That is a cast rather than a conversion — the handle carries
// the engine's own JSObject — which is why wrapper identity is unaffected.
public sealed partial class DomBridge : Dom.Features.IDocumentStructureHost
{
    JsValue Dom.Features.IDocumentStructureHost.ToJsObject(DomNode node) =>
        WrapNode(node);

    DomElement Dom.Features.IDocumentStructureHost.DocumentElement => DocumentElement;

    string Dom.Features.IDocumentStructureHost.Title
    {
        get => Title;
        set => Title = value;
    }
}
