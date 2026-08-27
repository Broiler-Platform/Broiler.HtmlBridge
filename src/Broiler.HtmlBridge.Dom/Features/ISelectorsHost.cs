using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="SelectorsBinding"/> needs from the bridge: the descendant selector
/// search (<c>querySelector</c>/<c>querySelectorAll</c> — it wraps every hit through the bridge's JS-object
/// cache), the by-tag descendant collector (<c>getElementsByTagName</c>) and the plain JS-wrapper factory
/// (<c>closest</c>). Selector matching itself (<c>MatchesSelector</c>) and the element-parent walk
/// (<c>ParentEl</c>) are the bridge's <c>internal static</c> helpers, called directly.
/// </summary>
internal interface ISelectorsHost
{
    /// <summary>The realm holding <c>NodeList</c>/<c>HTMLCollection</c>, or <see langword="null"/>
    /// before the bridge is attached.</summary>
    Broiler.JavaScript.Engine.JSContext? JsContext { get; }

    JSValue FindInDescendants(DomElement element, string selector, bool all);
    void CollectElementsByTagName(DomElement element, string tagName, List<JSValue> results);
    void CollectElementsByClassName(DomElement element, string classNames, List<JSValue> results);
    JSObject ToJSObject(DomNode node);
    // Selector matching moved onto the host (Phase 2 item 4 de-globalization): MatchesSelector reads
    // the per-bridge `:checked` state, so it is now a bridge-instance method rather than a static helper.
    bool MatchesSelector(DomElement element, string selector, DomElement? scope = null);
}
