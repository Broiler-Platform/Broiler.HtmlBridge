using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IMutationObserverHost"/>, the narrow
/// contract the extracted <see cref="Broiler.HtmlBridge.Dom.Features.MutationObserverBinding"/>
/// feature module consumes (HtmlBridge complexity-reduction roadmap Phase 3). Explicit interface
/// members, so these seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// This is the half-migrated seam for the MutationObserver slice: the module speaks JSEAL, the
/// wrapper cache the bridge keys on is still an engine one, and <see cref="JsInterop"/> is the cast
/// between them. It is a cast and not a conversion — a JSEAL object handle carries the engine's own
/// object — so a record's <c>target</c> is the same wrapper instance a page already holds.
/// </remarks>
public sealed partial class DomBridge : IMutationObserverHost
{
    IJsRealm IMutationObserverHost.Realm => Realm;

    JsValue IMutationObserverHost.WrapNode(DomNode node) => JsInterop.FromEngineObject(ToJSObject(node));

    DomNode? IMutationObserverHost.FindNode(JsValue wrapper) =>
        wrapper.IsObject ? FindDomNodeByJSObject(JsInterop.ToEngineObject(wrapper)) : null;

    // Mutation-observer delivery is driven off canonical DomDocument.Mutated (the observer binding
    // subscribes per observed document). The bridge suppresses delivery while it mutates the live
    // tree as an implementation detail — serialize/render attribute bakes, document re-parse — so
    // those mutations are not delivered to script observers and, critically, cannot re-enter script
    // synchronously mid-serialize. Depth-counted so nested suppressed regions compose.
    private int _mutationDeliverySuppressionDepth;

    bool IMutationObserverHost.MutationDeliverySuppressed => _mutationDeliverySuppressionDepth > 0;

    /// <summary>
    /// Opens a scope in which script-observable mutation-record delivery is suppressed (see
    /// <see cref="IMutationObserverHost.MutationDeliverySuppressed"/>). Use with <c>using</c>.
    /// </summary>
    internal MutationDeliverySuppressionScope SuppressMutationDelivery() => new(this);

    /// <summary>Depth-counted RAII scope toggling <see cref="_mutationDeliverySuppressionDepth"/>.</summary>
    internal readonly struct MutationDeliverySuppressionScope : IDisposable
    {
        private readonly DomBridge _bridge;

        internal MutationDeliverySuppressionScope(DomBridge bridge)
        {
            _bridge = bridge;
            _bridge._mutationDeliverySuppressionDepth++;
        }

        public void Dispose() => _bridge._mutationDeliverySuppressionDepth--;
    }
}
