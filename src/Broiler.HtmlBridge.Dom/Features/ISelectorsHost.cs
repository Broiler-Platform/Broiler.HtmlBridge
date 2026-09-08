using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="SelectorsBinding"/> needs from the bridge: selector validation,
/// the descendant selector search (<c>querySelector</c>/<c>querySelectorAll</c> — it wraps every hit
/// through the bridge's JS-object cache), the two live element collections
/// (<c>getElementsByTagName</c>/<c>getElementsByClassName</c>) and the plain JS-wrapper factory
/// (<c>closest</c>). Selector matching itself (<c>MatchesSelector</c>) reads the per-bridge
/// <c>:checked</c> state and so is a bridge-instance member too; the element-parent walk
/// (<c>ParentEl</c>) is an <c>internal static</c> bridge helper, called directly.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type. Where it used to hand the binding the bridge's script context
/// — so the binding could pass it straight back to <c>ValidateSelector</c> and to the collection
/// factory, which are the only two things it did with it — it now names the two operations that
/// needed it, and nothing else. That is the difference the
/// migration is after: the binding says what it wants done, and which engine does it is the bridge's
/// business.
/// </para>
/// <para>
/// <see cref="ElementsByTagName"/> and <see cref="ElementsByClassName"/> replace the pair of
/// "collect into this list" members for the same reason. Building an <c>HTMLCollection</c> — with the
/// liveness and the DOM §4.2.10.2 named getter that go with it — is still engine-typed work in
/// <c>DomCollectionBinding</c>, so the whole of it sits on the bridge side of the seam rather than
/// being reassembled from an engine-typed list the binding would have to hold.
/// </para>
/// </remarks>
internal interface ISelectorsHost
{
    /// <summary>
    /// Validates a selector argument per DOM §4.2.6, throwing <c>SyntaxError</c> when it does not
    /// parse as a selector list. A no-op before the bridge is attached, as it always has been —
    /// there is then no realm to raise a <c>DOMException</c> in.
    /// </summary>
    void ValidateSelector(string selector);

    /// <summary>
    /// The descendant selector search: the first hit (or <see cref="JsValue.Null"/>) when
    /// <paramref name="all"/> is <see langword="false"/>, and a static <c>NodeList</c> when it is
    /// <see langword="true"/>.
    /// </summary>
    JsValue FindInDescendants(DomElement element, string selector, bool all);

    /// <summary>A live <c>HTMLCollection</c> of <paramref name="element"/>'s descendants whose
    /// lower-cased tag name matches <paramref name="tagName"/>.</summary>
    JsValue ElementsByTagName(DomElement element, string tagName);

    /// <summary>A live <c>HTMLCollection</c> of <paramref name="element"/>'s descendants carrying
    /// every class in <paramref name="classNames"/>.</summary>
    JsValue ElementsByClassName(DomElement element, string classNames);

    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue ToWrapper(DomNode node);

    // Selector matching moved onto the host (Phase 2 item 4 de-globalization): MatchesSelector reads
    // the per-bridge `:checked` state, so it is now a bridge-instance method rather than a static helper.
    bool MatchesSelector(DomElement element, string selector, DomElement? scope = null);
}
