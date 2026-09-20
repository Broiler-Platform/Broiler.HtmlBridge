using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The two bridge seams <see cref="ElementTraversalBinding"/> needs: the realm its results are built
/// in, and the JS-wrapper factory that maps a canonical node to its cached wrapper. The views themselves
/// are the canonical <see cref="DomNode"/> element-traversal members, which need no host.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, so the wrapper factory is spelled <see cref="ToWrapper"/> and
/// answers a <see cref="JsValue"/>. It hands back the <em>same</em> cached wrapper the bridge caches
/// for the node, as a handle that carries the same object on every call, so
/// <c>el.firstElementChild === el.firstElementChild</c>.
/// </para>
/// <para>
/// <see cref="Realm"/> is here because building the <c>children</c> Array is the realm's business,
/// and the realm is the only place a binding can ask for one.
/// </para>
/// </remarks>
internal interface IElementTraversalHost
{
    /// <summary>The realm the wrappers and the <c>children</c> Array belong to.</summary>
    IJsRealm Realm { get; }

    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue ToWrapper(DomNode node);
}
