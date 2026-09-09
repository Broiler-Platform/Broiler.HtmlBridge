using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The bridge services <see cref="CustomElementsBinding"/> consumes: the DOM half — mint an element,
/// ask whether one is in the tree, find a node behind a wrapper, read a control's form owner and
/// disabled state — plus JS-wrapper identity and the realm the registry calls page code back through.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. What used to be the second kind of member on this contract — calling a JavaScript
/// constructor, calling a reaction, and the three promise factories <c>whenDefined</c> needed — is
/// gone: every one of them was an engine operation with no bridge state behind it, and
/// <see cref="IJsCalls.Construct"/>, <see cref="IJsCalls.Invoke"/> and
/// <see cref="IJsJobs.NewPromise"/> are the realm's own. The registry asks the realm directly, which
/// is why <see cref="Realm"/> is the one member that replaced six.
/// </para>
/// <para>
/// <b><c>whenDefined</c>'s pending promise is the case worth naming.</b> It used to be handed a
/// promise plus a <em>function object</em> wrapping the captured resolve delegate, because the only
/// way to keep a resolver was to close over an executor that happened to run synchronously.
/// <see cref="IJsJobs.NewPromise"/> hands the settle functions back, so the registry stores an
/// <c>Action</c> and never mints a function no page can reach.
/// </para>
/// </remarks>
internal interface ICustomElementsHost
{
    /// <summary>The realm a definition's constructor and its reactions are called in.</summary>
    IJsRealm Realm { get; }

    /// <summary>Every element in the document, in tree order — the set an upgrade sweeps.</summary>
    IReadOnlyList<DomElement> Elements { get; }

    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    /// <summary>The wrapper already minted for <paramref name="element"/>, if any. A reaction is
    /// only ever dispatched to an element a page has seen, so this never mints one.</summary>
    bool TryGetWrapper(DomElement element, out JsValue wrapper);

    /// <summary>The node behind a wrapper, for <c>customElements.upgrade(root)</c>.</summary>
    DomNode? FindNode(JsValue wrapper);

    DomElement CreateBridgeElement(string tagName);

    /// <summary>Whether the element is in the document tree, which is what decides
    /// <c>connectedCallback</c>.</summary>
    bool IsConnected(DomElement element);

    /// <summary>The element's form owner, or <see langword="null"/> — what
    /// <c>formAssociatedCallback</c> reports.</summary>
    DomElement? FormOwnerOf(DomElement element);

    /// <summary>Whether the element is disabled, by its own attribute or an ancestor
    /// <c>&lt;fieldset&gt;</c>'s — what <c>formDisabledCallback</c> reports.</summary>
    bool IsFormControlDisabled(DomElement element);
}
