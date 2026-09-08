using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IShadowDomHost implementation for the ShadowDomBinding feature module (Phase 3): the
// per-element shadow linkage (host/root/mode) stays on the bridge's Shadow runtime slot and is exposed
// here as named primitives — the existing-root lookup, the mode read, and a single AttachShadowRoot that
// creates the #shadow-root element, parents it and records the mode in one step. Explicit interface
// members, so these seams do not widen the public DomBridge surface.
//
// The contract is spelled in JSEAL now, and the script context it used to carry is gone: it was there
// only so the module could raise the second-attachment NotSupportedError, which IJsCalls.DomError owns.
// The realm member must be explicit — DomBridge.Realm is internal, so an implicit implementation of a
// public interface member cannot see it (CS0737).
public sealed partial class DomBridge : Dom.Features.IShadowDomHost
{
    IJsRealm Dom.Features.IShadowDomHost.Realm => Realm;

    DomElement? Dom.Features.IShadowDomHost.GetShadowRoot(DomElement element) => GetShadowRoot(element);

    bool Dom.Features.IShadowDomHost.TryGetShadowMode(DomElement element, out string mode)
    {
        if (ShadowStateFor(element).Mode.TryGet(out var raw) && raw is string s)
        {
            mode = s;
            return true;
        }

        mode = string.Empty;
        return false;
    }

    DomElement Dom.Features.IShadowDomHost.AttachShadowRoot(DomElement host, string mode)
    {
        _hasShadowRoots = true;
        var shadowRoot = CreateBridgeElement("#shadow-root");
        // SetParent links the shadow root to its host, so GetOwningDocument derives the shadow root's
        // owning document from the host's tree position — no OwnerDocRoot inheritance needed (P4.4c).
        SetParent(shadowRoot, host);
        ShadowStateFor(shadowRoot).Host.Set(host);
        ShadowStateFor(shadowRoot).Mode.Set(mode);
        ShadowStateFor(host).Root.Set(shadowRoot);
        ShadowStateFor(host).Mode.Set(mode);
        return shadowRoot;
    }

    // The bridge's own JSEAL-vocabulary wrapper factory: a handle over the object its wrapper cache
    // already holds, which is a cast rather than a conversion, so wrapper identity is unchanged.
    JsValue Dom.Features.IShadowDomHost.WrapNode(DomNode node) => WrapNode(node);
}
