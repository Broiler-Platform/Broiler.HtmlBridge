using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="MutationObserverBinding"/> feature module needs.
/// Mutation-record delivery only needs the realm
/// the records are built in and the JS-wrapper identity for a node, and the <c>observe()</c>
/// registration callback needs to resolve the observed target from its JS wrapper — nothing else of
/// the bridge is exposed.
/// </summary>
/// <remarks>
/// The contract names no engine type. The binding installs its constructor and host functions through
/// the realm: <see cref="IJsSource.EvaluateHostScript"/> for the script, and ordinary property writes
/// on <see cref="IJsRealm.Global"/> for the two host functions.
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
