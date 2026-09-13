using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IMutationObserverHost"/>, the narrow
/// contract the extracted <see cref="Broiler.HtmlBridge.Dom.Features.MutationObserverBinding"/>
/// feature module consumes (HtmlBridge complexity-reduction roadmap Phase 3). Explicit interface
/// members, so these seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// <b>The wrapper cache the bridge keys on is not an engine one, and this said it was.</b>
/// <c>Runtime/JsObjectRegistry</c> was re-typed onto <see cref="JsValue"/> and keys on
/// <see cref="JsValue.ObjectIdentity"/>; with the reverse lookup taking a handle as well there is no
/// cast left in this file. A record's <c>target</c> is the same wrapper instance a page already
/// holds because it is the same handle, not because one was unwrapped and re-wrapped around it.
/// </remarks>
public sealed partial class DomBridge : IMutationObserverHost
{
    IJsRealm IMutationObserverHost.Realm => Realm;

    JsValue IMutationObserverHost.WrapNode(DomNode node) => WrapNode(node);

    DomNode? IMutationObserverHost.FindNode(JsValue wrapper) =>
        wrapper.IsObject ? FindDomNodeByJSObject(wrapper) : null;

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
