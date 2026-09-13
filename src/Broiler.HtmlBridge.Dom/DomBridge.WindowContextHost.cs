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
/// Nothing here converts. The window and document are the bridge's roots, which are the handles the
/// realm minted, and the sub-document builder answers a handle of its own, so the window the manager
/// compares against is the same instance by construction rather than by a cast. (This used to call
/// the file the seam's engine-typed half and say all three were wrapped as handles here; the
/// sub-document builder was already forwarded as it stands.)
/// </remarks>
public sealed partial class DomBridge : IWindowContextHost
{
    IJsRealm? IWindowContextHost.Realm => _realm;

    JsValue IWindowContextHost.WindowObject => WindowHandle;

    // Undefined rather than the Missing the root holds, as the member's name says: its one consumer,
    // WindowContextManager.GetWindowDocument, answers undefined on its other branch too.
    JsValue IWindowContextHost.MainDocumentOrUndefined =>
        DocumentHandle.IsMissing ? JsValue.Undefined : DocumentHandle;

    JsValue IWindowContextHost.GetOrCreateSubDocument(DomElement container) =>
        GetOrCreateSubDocument(container);
}
