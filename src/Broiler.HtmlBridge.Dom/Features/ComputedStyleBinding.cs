using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

// Engine-typed only for the two adapters at the foot of this file, whose callers are unmigrated
// registration sites with engine call frames: DomBridge/Registration/Registration.cs installs
// window.getComputedStyle, and DomBridge/ElementInterfaces.cs installs <img>.width/.height.
using Broiler.JavaScript.Runtime;

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
/// <remarks>
/// The JavaScript vocabulary is JSEAL's, so the bodies name no engine type. What has not moved is the
/// argument read: both entry points are registered from files that have not migrated, so their call
/// frames are still engine ones and the adapters at the foot of this file do the read before handing the
/// values on. The coercions are unchanged — the same ECMAScript <c>ToString</c> on the same argument —
/// and they move into the bodies when those registration sites migrate.
/// </remarks>
internal static class ComputedStyleBinding
{
    /// <summary>
    /// <c>window.getComputedStyle(element, pseudoElement)</c> — the used-value declaration for
    /// <paramref name="target"/>'s element, or an empty object when the call named no element at all.
    /// </summary>
    internal static JsValue GetComputedStyle(IComputedStyleHost host, JsValue target, string? pseudoElement)
    {
        var el = target.IsObject ? host.FindElement(target) : null;
        return host.BuildComputedStyle(el, pseudoElement);
    }

    /// <summary>
    /// The <c>&lt;img&gt;.width</c> / <c>&lt;img&gt;.height</c> IDL getter — the used (rendered)
    /// dimension read out of computed style, falling back to the raw content attribute, then <c>0</c>.
    /// </summary>
    internal static JsValue GetUsedDimension(IComputedStyleHost host, string? dimName, DomElement element)
    {
        // First check computed style for this element.
        var computed = host.BuildComputedStyle(element, null);
        var csVal = host.Realm.GetProperty(computed, dimName!);
        if (!csVal.IsMissing && !csVal.IsNull && !csVal.IsUndefined)
        {
            // The realm's ToString, not the handle's: this is the observable ECMAScript coercion, and a
            // computed value is a string the declaration produced rather than one this module minted.
            var cssStr = host.Realm.ToJsString(csVal);
            if (!string.IsNullOrEmpty(cssStr))
            {
                var px = DomBridge.ParseCssLengthToPixels(cssStr);
                if (!double.IsNaN(px))
                    return JsValue.Number(px);
            }
        }

        // Fallback: HTML attribute
        if (DomBridge.TryGetAttribute(element, dimName, out var attrVal) && double.TryParse(attrVal, out var attrNum))
            return JsValue.Number(attrNum);
        return JsValue.Number(0);
    }

    // -------- engine-typed adapters (see the remarks on this class) --------

    /// <summary>
    /// <c>window.getComputedStyle</c> as its unmigrated registration site calls it.
    /// </summary>
    /// <remarks>
    /// The empty-object answer for a call with no arguments is minted through the realm rather than as an
    /// engine object, so that the one object this operation can produce without an element comes from the
    /// same place every other one does.
    /// </remarks>
    public static JSValue GetComputedStyle(IComputedStyleHost host, in Arguments a)
    {
        if (a.Length == 0)
            return Runtime.JsInterop.ToEngineObject(host.Realm.NewObject());

        var target = a[0] is JSObject targetObj ? Runtime.JsInterop.FromEngineObject(targetObj) : JsValue.Undefined;
        var pseudoElement = a.Length > 1 ? a[1]?.ToString() : null;
        var computed = GetComputedStyle(host, target, pseudoElement);
        return Runtime.JsInterop.ToEngineObject(computed);
    }

    /// <summary><c>&lt;img&gt;.width</c>/<c>.height</c> as its unmigrated registration site calls it.</summary>
    public static JSValue GetUsedDimension(IComputedStyleHost host, string? dimName, DomElement element, in Arguments _)
    {
        var used = GetUsedDimension(host, dimName, element);

        // The migrated body answers a number and only a number, so the handle carries it inline and there
        // is nothing for JsInterop to unwrap.
        return new Broiler.JavaScript.BuiltIns.Number.JSNumber(used.AsNumber);
    }
}
