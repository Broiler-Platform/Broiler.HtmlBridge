using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The two bridge seams <see cref="ElementTraversalBinding"/> needs: the realm its results are built
/// in, and the JS-wrapper factory that maps a canonical node to its cached wrapper. The element-child
/// enumeration (<c>ChildElements</c>), the element-parent walk (<c>ParentEl</c>) and the text-node
/// test (<c>IsText</c>) are the bridge's <c>internal static</c> helpers, called directly.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, so the wrapper factory is spelled <see cref="ToWrapper"/> —
/// rather than after the engine's own object class, as it was — and answers a
/// <see cref="JsValue"/>. It hands back the <em>same</em> cached
/// wrapper instance the bridge has always handed out — a handle carries the engine's own object — so
/// <c>el.firstElementChild === el.firstElementChild</c> is the same question it was.
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
