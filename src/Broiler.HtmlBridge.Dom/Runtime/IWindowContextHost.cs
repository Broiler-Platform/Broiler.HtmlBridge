using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The narrow surface <see cref="WindowContextManager"/> needs from the bridge: the realm the context
/// switch reads and writes its globals in, the top-level window and document objects, and the
/// sub-document builder a target window resolves its document through. Implemented by <c>DomBridge</c>
/// via explicit interface members (see <c>DomBridge.WindowContextHost.cs</c>). The sub-window identity
/// and owner-window state are not part of this contract — the manager reads them from the
/// <see cref="BrowsingContextManager"/> and <see cref="EventTargetRegistry"/> it holds directly.
/// </summary>
/// <remarks>
/// The whole contract is spelled in JSEAL, so nothing here names an engine type. The three members the
/// engine context used to be reached through — <c>Eval</c>, <c>GetGlobal</c> and <c>SetGlobal</c> —
/// are gone: all three are operations on <see cref="IJsRealm"/> (<c>EvaluateHostScript</c>, and
/// get/set on <see cref="IJsRealm.Global"/>, which under an engine whose global <em>is</em> its
/// variable scope is the same binding a bare <c>window = …</c> would write). <see cref="Realm"/> is
/// nullable for the reason the old <c>HasJsContext</c> flag existed: the bridge has no realm before
/// <c>Attach</c> and none after teardown, and the context switch is expected to degrade to running its
/// callback rather than to throw.
/// </remarks>
internal interface IWindowContextHost
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

    /// <summary>The sub-document object for a container (a target window's <c>document</c>).</summary>
    JsValue GetOrCreateSubDocument(DomElement container);
}
