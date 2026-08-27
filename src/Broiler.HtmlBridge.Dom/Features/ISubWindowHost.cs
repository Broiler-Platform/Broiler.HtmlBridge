using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow surface the co-located sub-window feature (<see cref="SubWindowBinding"/>, P3.17) needs
/// from the bridge: the top-level window object, the sub-document builder it wraps (mutual recursion),
/// the browsing-context tree/link queries, sub-resource URL resolution, the scroll-geometry read/write
/// helpers, computed-style construction, and the global event constructors. Implemented by
/// <c>DomBridge</c> via explicit interface members (see <c>DomBridge.SubWindowHost.cs</c>), so the module
/// reaches no arbitrary bridge private field. The module holds direct references to the shared owners it
/// uses (<c>BrowsingContextManager</c>, <c>EventTargetRegistry</c>, <c>MessagingBinding</c>); those are
/// not part of this contract.
/// </summary>
internal interface ISubWindowHost
{
    /// <summary>The top-level window JS object (the sub-window's <c>top</c>, and its <c>parent</c> when
    /// the sub-document is not itself nested).</summary>
    JSObject? WindowJSObject { get; }

    /// <summary>The sub-document JS object for a container (built on demand). The sub-window's
    /// <c>document</c> getter and <c>defaultView</c> wiring depend on it.</summary>
    JSObject GetOrCreateSubDocument(DomElement container);

    /// <summary>The severed content document of a nested-browsing-context container, or <c>null</c>.</summary>
    DomDocument? GetContentDocument(DomElement container);

    /// <summary>The frame element a severed sub-document belongs to (for parent-window resolution).</summary>
    DomElement? GetFrameForContentDocument(DomNode? owningDocument);

    /// <summary>Resolves a sub-resource URL against a base URL (for the sub-window <c>location</c>).</summary>
    string ResolveSubResourceUrl(string resourceUrl, string? baseUrl);

    /// <summary>The base URL a sub-document inherits from its ancestor browsing contexts.</summary>
    string GetInheritedSubDocumentBaseUrl(DomElement container);

    /// <summary>The current scroll offset of an element along one axis (px).</summary>
    double GetElementScrollOffset(DomElement element, bool vertical);

    /// <summary>Applies a scroll offset to an element (the sub-window scroll/scrollTo/scrollBy path).
    /// Wraps the bridge's scroll setter with the sub-window's fixed <c>clamp: false</c>.</summary>
    void SetElementScroll(DomElement element, double? left, double? top, bool relative, string? behavior);

    /// <summary>Parses <c>scroll(x,y)</c> / <c>scroll({left,top,behavior})</c> arguments.</summary>
    (double? Left, double? Top, string? Behavior) GetScrollArguments(in Arguments args);

    /// <summary>The DOM element a JS object wraps, or <c>null</c> (for <c>getComputedStyle</c>).</summary>
    DomElement? FindDomElementByJSObject(JSObject jsObj);

    /// <summary>Builds the read-only computed-style JS object for an element (sub-window
    /// <c>getComputedStyle</c>), resolving the sub-document's own stylesheets.</summary>
    JSObject BuildComputedStyleObject(DomElement? element, string? pseudoElement);

    /// <summary>A global constructor/value from the JS context (the sub-window mirrors the event
    /// constructors — <c>Event</c>, <c>MouseEvent</c>, … — and <c>MessageChannel</c>), or <c>null</c>
    /// when the context has no such global (or no context).</summary>
    JSValue? GetGlobal(string name);

    /// <summary>Publishes on <paramref name="subWindow"/> whatever this frame's own scripts declared
    /// while it was being built, so a parent page can reach them as <c>frames[0].window.foo</c>.
    /// See <c>DomBridge.SubDocumentGlobals.cs</c>.</summary>
    void PublishPendingSubDocumentGlobals(DomElement containerElement, JSObject subWindow);
}
