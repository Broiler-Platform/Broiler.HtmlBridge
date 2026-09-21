using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The narrow surface <see cref="WindowContextManager"/> needs from the bridge: the realm the context
/// switch reads and writes its globals in, the top-level window and document objects, and the
/// sub-document builder a target window resolves its document through. Implemented by <c>DomBridge</c>
/// via explicit interface members (see <c>DomBridge/Hosts.Window.cs</c>). The sub-window identity
/// and owner-window state are not part of this contract — the manager reads them from the
/// <see cref="BrowsingContextManager"/> and <see cref="EventTargetRegistry"/> it holds directly.
/// </summary>
/// <remarks>
/// The whole contract is spelled in JSEAL, so nothing here names an engine type. Evaluation and
/// global get/set are operations on <see cref="IJsRealm"/> (<c>EvaluateHostScript</c>, and get/set on
/// <see cref="IJsRealm.Global"/>, which under an engine whose global <em>is</em> its variable scope is
/// the same binding a bare <c>window = …</c> would write). <see cref="Realm"/> is nullable because
/// the bridge has no realm before <c>Attach</c> and none after teardown, and the context switch is
/// expected to degrade to running its callback rather than to throw.
/// </remarks>
internal interface IWindowContextHost : Features.ISubDocumentFactoryHost
{
    /// <summary>
    /// The realm the window/document/location/parent/postMessage/self/top bindings live in, or
    /// <see langword="null"/> when no document is attached.
    /// </summary>
    IJsRealm? Realm { get; }

    /// <summary>The top-level window object (the identity a candidate is canonicalised against, and the
    /// <c>top</c> the context switch restores to), or <see cref="JsValue.Missing"/> when there is
    /// none.</summary>
    JsValue WindowObject { get; }

    /// <summary>The top-level document object, or <c>undefined</c> when absent.</summary>
    JsValue MainDocumentOrUndefined { get; }
}
