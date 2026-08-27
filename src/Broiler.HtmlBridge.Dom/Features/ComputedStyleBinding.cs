using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The computed-style reads, co-located as an HtmlBridge feature module (Phase 3): the CSSOM entry point
/// <c>window.getComputedStyle(element, pseudoElement?)</c> (which resolves an element's used-value style
/// declaration), and the <c>&lt;img&gt;.width</c>/<c>&lt;img&gt;.height</c> IDL getters, which report the
/// element's used (rendered) dimension by reading it out of the same computed-style object, falling back to
/// the content attribute and then <c>0</c>. Both reach the used-value engine through the narrow
/// <see cref="IComputedStyleHost"/> contract; the content-attribute fallback and CSS-length parse use the
/// bridge's neutral <c>internal static</c> <c>TryGetAttribute</c>/<c>ParseCssLengthToPixels</c> helpers
/// directly. <c>GetComputedStyle</c> was the bridge's <c>JsRegistrationGetComputedStyle121Core</c>;
/// <c>GetUsedDimension</c> was <c>JsElementInterfacesCallback062Core</c>.
/// </summary>
internal static class ComputedStyleBinding
{
    public static JSValue GetComputedStyle(IComputedStyleHost host, in Arguments a)
    {
        if (a.Length == 0)
            return new JSObject();
        var targetObj = a[0] as JSObject;
        var el = targetObj != null ? host.FindDomElementByJSObject(targetObj) : null;
        var pseudoElement = a.Length > 1 ? a[1]?.ToString() : null;
        return host.BuildComputedStyleObject(el, pseudoElement);
    }

    // <img>.width / <img>.height IDL getter — the used (rendered) dimension read out of computed style,
    // falling back to the raw content attribute, then 0.
    public static JSValue GetUsedDimension(IComputedStyleHost host, string? dimName, DomElement element, in Arguments _)
    {
        // First check computed style for this element
        var computed = host.BuildComputedStyleObject(element, null);
        var csVal = computed[(KeyString)dimName];
        if (csVal != null && !csVal.IsNull && !csVal.IsUndefined)
        {
            var cssStr = csVal.ToString();
            if (!string.IsNullOrEmpty(cssStr))
            {
                var px = DomBridge.ParseCssLengthToPixels(cssStr);
                if (!double.IsNaN(px))
                    return new JSNumber(px);
            }
        }

        // Fallback: HTML attribute
        if (DomBridge.TryGetAttribute(element, dimName, out var attrVal) && double.TryParse(attrVal, out var attrNum))
            return new JSNumber(attrNum);
        return new JSNumber(0);
    }
}
