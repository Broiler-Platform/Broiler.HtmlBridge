using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit INodeAccessorsHost implementation for the NodeAccessorsBinding feature module (Phase 3):
// the bridge exposes the JS-wrapper factory, the document node, the tree-root walk, the notifying
// character-data setter and the two document-wrapper lookups via explicit interface members, so the
// module reaches no arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.INodeAccessorsHost
{
    JSObject Dom.Features.INodeAccessorsHost.ToJSObject(DomNode node) => ToJSObject(node);

    Broiler.JavaScript.Engine.JSContext? Dom.Features.INodeAccessorsHost.JsContext => _jsContext;

    DomNode Dom.Features.INodeAccessorsHost.DocumentNode => _document;

    DomNode Dom.Features.INodeAccessorsHost.GetTreeRoot(DomNode node) => GetTreeRoot(node);

    void Dom.Features.INodeAccessorsHost.SetCharacterData(DomNode node, string? value)
        => SetCharacterData(node, value);

    bool Dom.Features.INodeAccessorsHost.TryGetDocumentWrapper(DomNode documentRoot, out JSObject wrapper)
        => _jsObjects.TryGetDocument(documentRoot, out wrapper);

    JSObject? Dom.Features.INodeAccessorsHost.DocumentJSObject => _documentJSObject;
}
