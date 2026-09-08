using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ComputedStyleBinding"/> needs from the bridge: the realm the
/// computed-style object belongs to, the JS-wrapper reverse lookup (a JS element object → its canonical
/// <see cref="DomElement"/>) and the computed-style object builder (resolving the element's used values,
/// optionally for a pseudo-element).
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type. The wrapper lookup takes a <see cref="JsValue"/>, and is named
/// for what it does rather than for the engine type it used to take: a member named after that type
/// could never grep to zero, and the round-1 rename of <see cref="ITraversalHost"/>'s lookups is the
/// pattern this follows.
/// </para>
/// <para>
/// <see cref="Realm"/> is here because <see cref="ComputedStyleBinding.GetUsedDimension"/> reads a
/// property off the computed-style object outside any call frame — <c>&lt;img&gt;.width</c> asks the
/// declaration for <c>width</c> — and a realm is what a property read needs. It is the same realm the
/// object it reads was built in, which is what makes the read the one a page would have made.
/// </para>
/// </remarks>
internal interface IComputedStyleHost
{
    /// <summary>The realm the bridge is attached to.</summary>
    IJsRealm Realm { get; }

    /// <summary>The canonical element behind a JS wrapper, or <see langword="null"/> for anything else.</summary>
    DomElement? FindElement(JsValue wrapper);

    /// <summary>
    /// The read-only <c>CSSStyleDeclaration</c> of <paramref name="element"/>'s used values, optionally
    /// for a pseudo-element. A <see langword="null"/> element answers an empty declaration, as it always
    /// has.
    /// </summary>
    JsValue BuildComputedStyle(DomElement? element, string? pseudoElement);
}
