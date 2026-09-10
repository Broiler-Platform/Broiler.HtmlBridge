using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IWindowContextHost"/>, the contract the
/// <see cref="WindowContextManager"/> owner consumes (HtmlBridge complexity-reduction roadmap Phase 3,
/// P3.18). Explicit interface members, so these realm seams do not widen the public
/// <c>DomBridge</c> surface — and <see cref="DomBridge.Realm"/> is internal, so an implicit
/// implementation of <see cref="IWindowContextHost.Realm"/> would not compile.
/// </summary>
/// <remarks>
/// This file is the seam's engine-typed half, and only where the bridge's own state is still engine
/// typed: the top-level window/document objects and the sub-document builder are fields and methods
/// other groups' files hold as the engine's own object, so each is wrapped as a handle here. That is a cast
/// — a JSEAL handle carries the engine's own object — so the window identity the manager compares and
/// the one the browsing-context maps are keyed on stay the same instance.
/// </remarks>
public sealed partial class DomBridge : IWindowContextHost
{
    IJsRealm? IWindowContextHost.Realm => _realm;

    JsValue IWindowContextHost.WindowObject =>
        _windowJSObject is { } window ? Dom.Runtime.JsInterop.FromEngineObject(window) : JsValue.Missing;

    JsValue IWindowContextHost.MainDocumentOrUndefined =>
        _documentJSObject is { } document ? Dom.Runtime.JsInterop.FromEngineObject(document) : JsValue.Undefined;

    JsValue IWindowContextHost.GetOrCreateSubDocument(DomElement container) =>
        GetOrCreateSubDocument(container);
}
