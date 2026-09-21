using Broiler.JSeal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

// ════════════════════════════════════════════════════════════════════════════════════════════
// THE PRIMITIVES THE NARROW HOST CONTRACTS SHARE.
//
// Each feature module gets its own I*Host contract naming only what that module needs, and that
// stays true. But a handful of primitives are needed by most of them, and every contract used to
// re-declare its own copy — the realm in 25 of them, WrapNode in 18 — which DomBridge then had to
// implement explicitly once per contract, forwarding each copy to the same member.
//
// Each interface below carries ONE primitive (or a pair that is always wanted together) and is
// inherited by exactly the contracts that already declared it. Nothing widens: a contract that
// never asked for WrapNode does not inherit INodeWrapperHost, and a feature module still sees only
// what its own contract names. What changes is that the primitive is declared once and implemented
// once.
//
// A primitive belongs here only when every contract that declares it is satisfied by the SAME
// implementation. Several are deliberately absent because they are not:
//
//   Realm again, as IJsRealm? — IWorkerHost and IWindowContextHost want the field, null before
//     Attach; the 25 below want the property, which throws instead. Different type, different
//     answer for an unattached bridge.
//   FindElement / FindNode — three contracts guard with `wrapper.IsObject` and three do not.
//   CreateBridgeElement / CreateBridgeElementNS — IDocumentFactoryHost marks what it makes with
//     NoteCreatedByScript; the others do not.
//   DispatchWindowEvent — IWindowEventTargetHost answers a JsValue, the others a bool.
//   DocumentWrapper, WindowObject — one contract of each pair maps a missing handle to JsValue.Null.
// ════════════════════════════════════════════════════════════════════════════════════════════

/// <summary>The realm a binding mints and reads JavaScript values in.</summary>
/// <remarks>
/// This is the property, which throws when the bridge is not attached. A contract that has to cope
/// with an unattached bridge declares <c>IJsRealm?</c> of its own instead and reads the field.
/// </remarks>
internal interface IRealmHost
{
    IJsRealm Realm { get; }
}

/// <summary>The bridge's wrapper for a node — minted once per node and reused.</summary>
internal interface INodeWrapperHost
{
    JsValue WrapNode(DomNode node);
}

/// <summary>
/// The same wrapper, under the name the older contracts reach it by.
/// <para>
/// One object per node, for the life of that node: the handle is cached, so
/// <c>document.body === document.body</c> answers true. The registry's reverse lookup keys on
/// <see cref="JsValue.ObjectIdentity"/>, which is what makes that cache findable from the JS side.
/// </para>
/// </summary>
internal interface IJsObjectHost
{
    JsValue ToJsObject(DomNode node);
}

/// <summary>Recomputing style for the scope a mutation touched.</summary>
internal interface IStyleInvalidationHost
{
    void InvalidateStyleScope(DomElement anchor);
}

/// <summary>The document's own node — the root a range created without an explicit one belongs to.</summary>
internal interface IDocumentNodeHost
{
    DomNode DocumentNode { get; }
}

/// <summary>The document's root element — <c>documentElement</c>, the <c>&lt;html&gt;</c> of a page.</summary>
internal interface IDocumentElementHost
{
    DomElement DocumentElement { get; }
}

/// <summary>Every element in the document, in tree order — the set a custom-element upgrade sweeps.</summary>
internal interface IElementsHost
{
    IReadOnlyList<DomElement> Elements { get; }
}

/// <summary>The URL the page was loaded from, which relative URLs resolve against.</summary>
internal interface IPageUrlHost
{
    string PageUrl { get; }
}

/// <summary>
/// Web IDL's name checks, raised through the realm as the DOM's own exceptions:
/// <c>InvalidCharacterError</c> when a name does not match the Name production, and
/// <c>NamespaceError</c> when a qualified name and its namespace disagree.
/// </summary>
internal interface INameValidationHost
{
    void ValidateElementName(string name);

    void ValidateQualifiedName(string qualifiedName, string? ns);
}

/// <summary>
/// Selector matching, and the parse check that rejects a bad selector the same way.
/// <para>
/// <see cref="ValidateSelector"/> returns quietly for a valid selector list and raises a
/// <c>SyntaxError</c> <c>DOMException</c> otherwise (DOM §4.2.6). It is a no-op before the bridge
/// is attached: there is then no realm to raise one through.
/// </para>
/// </summary>
internal interface ISelectorMatchHost
{
    bool MatchesSelector(DomElement element, string selector, DomElement? scope = null);

    void ValidateSelector(string selector);
}

/// <summary>Making a text node that belongs to this bridge's document.</summary>
internal interface ITextNodeFactoryHost
{
    DomText CreateBridgeTextNode(string data);
}

/// <summary>Placing a node at an index, with the bookkeeping a bare tree insert would skip.</summary>
internal interface INodeInsertionHost
{
    void InsertNodeAt(DomNode parent, DomNode node, int index);
}

/// <summary>
/// The document object for a frame or object element, made on first ask and cached after.
/// A sub-window's <c>document</c> getter and its <c>defaultView</c> wiring both read it.
/// </summary>
internal interface ISubDocumentFactoryHost
{
    JsValue GetOrCreateSubDocument(DomElement container);
}
