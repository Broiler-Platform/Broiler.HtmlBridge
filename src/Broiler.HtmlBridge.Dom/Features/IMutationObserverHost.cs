using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="MutationObserverBinding"/> feature module needs
/// (HtmlBridge complexity-reduction roadmap Phase 3). Mutation-record delivery only needs the realm
/// the records are built in and the JS-wrapper identity for a node, and the <c>observe()</c>
/// registration callback needs to resolve the observed target from its JS wrapper — nothing else of
/// the bridge is exposed.
/// </summary>
/// <remarks>
/// The contract names no engine type. Where the binding was handed the bridge's script context to
/// install its constructor and host functions, it now asks the realm for both — <c>Eval</c> became
/// <see cref="IJsSource.EvaluateHostScript"/> and the two <c>context["…"] = fn</c> writes became
/// ordinary property writes on <see cref="IJsRealm.Global"/>, which under this engine is the same
/// object the context was.
/// </remarks>
internal interface IMutationObserverHost
{
    /// <summary>
    /// The realm the <c>MutationObserver</c> constructor, its host bridge functions and every
    /// mutation record are built in.
    /// </summary>
    IJsRealm Realm { get; }

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    /// <summary>Resolves the canonical node behind a JS wrapper, or null.</summary>
    DomNode? FindNode(JsValue wrapper);

    /// <summary>
    /// Whether script-observable mutation-record delivery is currently suppressed. Set while the
    /// bridge mutates the live tree internally (serialize/render bakes, parse) so those
    /// implementation-detail mutations — which fire canonical <see cref="DomDocument.Mutated"/>
    /// records — are not delivered to script observers (and cannot re-enter script mid-serialize).
    /// </summary>
    bool MutationDeliverySuppressed { get; }
}
