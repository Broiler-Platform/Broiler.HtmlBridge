using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The bridge services <see cref="CustomElementsBinding"/> consumes: the DOM half — mint an element,
/// ask whether one is in the tree, find a node behind a wrapper, read a control's form owner and
/// disabled state — plus JS-wrapper identity and the realm the registry calls page code back through.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. Calling a definition's constructor, calling a reaction and minting a promise are engine
/// operations with no bridge state behind them, so the registry asks the realm directly through
/// <see cref="IJsCalls.Construct"/>, <see cref="IJsCalls.Invoke"/> and
/// <see cref="IJsJobs.NewPromise"/>; <see cref="IRealmHost.Realm"/> is the only member this contract
/// needs for them.
/// </para>
/// <para>The inherited <see cref="IElementsHost.Elements"/> is the set an upgrade sweeps.</para>
/// <para>
/// <b><c>whenDefined</c>'s pending promise is the case worth naming.</b>
/// <see cref="IJsJobs.NewPromise"/> hands the settle functions back, so the registry stores an
/// <c>Action</c> and never mints a function no page can reach. The alternative — keeping a resolver
/// by closing over an executor that happens to run synchronously — costs a function object for
/// nothing.
/// </para>
/// </remarks>
internal interface ICustomElementsHost : IElementsHost, INodeWrapperHost, IRealmHost
{
    /// <summary>The wrapper already minted for <paramref name="element"/>, if any. A reaction is
    /// only ever dispatched to an element a page has seen, so this never mints one.</summary>
    bool TryGetWrapper(DomElement element, out JsValue wrapper);

    /// <summary>The node behind a wrapper, for <c>customElements.upgrade(root)</c>.</summary>
    DomNode? FindNode(JsValue wrapper);

    DomElement CreateBridgeElement(string tagName);

    /// <summary>Whether the element is connected — its shadow-including root is a document, any
    /// document rather than only the page's (DOM §4.2.2) — which is what decides
    /// <c>connectedCallback</c>.</summary>
    bool IsConnected(DomElement element);

    /// <summary>The element's form owner, or <see langword="null"/> — what
    /// <c>formAssociatedCallback</c> reports.</summary>
    DomElement? FormOwnerOf(DomElement element);

    /// <summary>Whether the element is disabled, by its own attribute or an ancestor
    /// <c>&lt;fieldset&gt;</c>'s — what <c>formDisabledCallback</c> reports.</summary>
    bool IsFormControlDisabled(DomElement element);
}
