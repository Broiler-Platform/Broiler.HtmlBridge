using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IDocumentStructureHost implementation for the DocumentStructureBinding feature module
// (Phase 3): the bridge exposes the document root, the JS-wrapper factory and the document title via
// explicit interface members, so the module never reaches an arbitrary bridge private field and the
// public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IDocumentStructureHost
{
    JSObject Dom.Features.IDocumentStructureHost.ToJSObject(DomNode node) => ToJSObject(node);

    DomElement Dom.Features.IDocumentStructureHost.DocumentElement => DocumentElement;

    string Dom.Features.IDocumentStructureHost.Title
    {
        get => Title;
        set => Title = value;
    }
}
