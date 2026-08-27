using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="MutationObserverBinding"/> feature module needs
/// (HtmlBridge complexity-reduction roadmap Phase 3). Mutation-record delivery only needs the
/// JS-wrapper identity for a node, and the <c>observe()</c> registration callback needs to resolve
/// the observed target from its JS wrapper — nothing else of the bridge is exposed.
/// </summary>
internal interface IMutationObserverHost
{
    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JSObject ToJSObject(DomNode node);

    /// <summary>Resolves the canonical node behind a JS wrapper, or null.</summary>
    DomNode? FindDomNodeByJSObject(JSObject? jsObj);

    /// <summary>
    /// Whether script-observable mutation-record delivery is currently suppressed. Set while the
    /// bridge mutates the live tree internally (serialize/render bakes, parse) so those
    /// implementation-detail mutations — which fire canonical <see cref="DomDocument.Mutated"/>
    /// records — are not delivered to script observers (and cannot re-enter script mid-serialize).
    /// </summary>
    bool MutationDeliverySuppressed { get; }
}
