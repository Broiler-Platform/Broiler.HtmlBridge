using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The two bridge seams <see cref="ElementTraversalBinding"/> needs: the realm its results are built
/// in, and the JS-wrapper factory that maps a canonical node to its cached wrapper. The views themselves
/// are the canonical <see cref="DomNode"/> element-traversal members, which need no host.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, so the wrapper factory is spelled <see cref="ToWrapper"/> —
/// rather than after the engine's own object class, as it was — and answers a
/// <see cref="JsValue"/>. It hands back the <em>same</em> cached
/// wrapper the bridge has always handed out, now as a handle that carries the same object on every
/// call, so <c>el.firstElementChild === el.firstElementChild</c> is the same question it was.
/// </para>
/// <para>
/// <see cref="Realm"/> is new to the contract because <c>children</c> used to construct its Array
/// directly with an engine type; building it is now the realm's business, and the realm is the only
/// place a binding can ask for one.
/// </para>
/// </remarks>
internal interface IElementTraversalHost
{
    /// <summary>The realm the wrappers and the <c>children</c> Array belong to.</summary>
    IJsRealm Realm { get; }

    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue ToWrapper(DomNode node);
}
