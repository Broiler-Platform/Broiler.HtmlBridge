using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IDocumentStructureHost implementation for the DocumentStructureBinding feature module
// (Phase 3): the bridge exposes the document root, the JS-wrapper factory and the document title via
// explicit interface members, so the module never reaches an arbitrary bridge private field and the
// public surface is unchanged.
//
// The contract is spelled in JSEAL and so is the bridge member behind it: ToJsObject forwards to
// WrapNode, which answers the handle the realm minted, and nothing here converts. This said the file
// was the seam half the migration left behind, where an unmigrated ToJSObject handed back an engine
// object for JsInterop to wrap. 5282d02 made ToJSObject a cast over WrapNode; bcce315 retired the cast
// and made this member forward to WrapNode.
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
