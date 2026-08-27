using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The shadow-DOM JS-binding members — the <c>element.shadowRoot</c> getter and
/// <c>element.attachShadow()</c> method — registered on every element wrapper, co-located as an
/// HtmlBridge feature module (Phase 3). The getter exposes an attached root only when its mode is
/// <c>open</c>; <c>attachShadow</c> rejects a second attachment (<c>NotSupportedError</c>), normalizes
/// the requested mode to <c>open</c>/<c>closed</c>, and creates + links the root through the
/// <see cref="IShadowDomHost"/> contract — the per-element shadow linkage stays the bridge's
/// <c>ElementRuntimeState.Shadow</c> slot (reached only through named primitives, the P3.7 pattern). Was
/// the bridge's <c>JsJsObjectsGetShadowRoot019Core</c> / <c>AttachShadow087Core</c>.
/// </summary>
internal static class ShadowDomBinding
{
    public static JSValue GetShadowRoot(IShadowDomHost host, DomElement element, in Arguments _)
    {
        var shadowRoot = host.GetShadowRoot(element);
        if (shadowRoot == null)
            return JSNull.Value;
        var mode = host.TryGetShadowMode(element, out var rawMode) ? rawMode : null;
        return string.Equals(mode, "open", StringComparison.OrdinalIgnoreCase) ? host.ToJSObject(shadowRoot) : JSNull.Value;
    }

    public static JSValue AttachShadow(IShadowDomHost host, DomElement element, in Arguments a)
    {
        if (host.GetShadowRoot(element) != null)
            DomBridge.ThrowDOMException(host.JsContext!, "Shadow root already attached.", "NotSupportedError");
        var mode = "open";
        if (a.Length > 0 && a[0] is JSObject options)
        {
            var modeValue = options[(KeyString)"mode"];
            if (modeValue != null && !modeValue.IsUndefined && !modeValue.IsNull)
            {
                mode = modeValue.ToString();
            }
        }

        mode = string.Equals(mode, "closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open";
        var shadowRoot = host.AttachShadowRoot(element, mode);
        return host.ToJSObject(shadowRoot);
    }
}
