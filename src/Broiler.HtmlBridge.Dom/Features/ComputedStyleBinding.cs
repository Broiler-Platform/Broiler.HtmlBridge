using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

// Engine-typed only for the one adapter at the foot of this file, whose caller is an unmigrated
// registration site with an engine call frame: DomBridge/ElementInterfaces.cs installs
// <img>.width/.height.

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
/// The JavaScript vocabulary is JSEAL's, so the bodies name no engine type. <c>getComputedStyle</c> now
/// reads its own call frame — its installer mints it through the realm — and the coercion of the
/// pseudo-element argument is the realm's <c>ToJsString</c>, which is the same observable ECMAScript
/// <c>ToString</c> the engine's <c>ToString()</c> ran there. What has not moved is
/// <c>&lt;img&gt;.width</c>/<c>.height</c>: <c>DomBridge/ElementInterfaces.cs</c> installs those and
/// still hands over an engine argument frame, so the one adapter at the foot of this file does the
/// unwrapping until it migrates.
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

    /// <summary>
    /// <c>window.getComputedStyle(element, pseudoElement)</c> as its installer calls it.
    /// </summary>
    /// <remarks>
    /// The empty-object answer for a call with no arguments is minted through the realm, so that the one
    /// object this operation can produce without an element comes from the same place every other one
    /// does. A first argument that is not an object stands in as <c>undefined</c>, which the body reads
    /// as "no element" exactly as the narrowing cast this replaces did.
    /// </remarks>
    public static JsValue GetComputedStyle(IComputedStyleHost host, in JsCall call)
    {
        if (call.Length == 0)
            return host.Realm.NewObject();

        var target = call[0].IsObject ? call[0] : JsValue.Undefined;
        var pseudoElement = call.Length > 1 ? call.Realm.ToJsString(call[1]) : null;
        return GetComputedStyle(host, target, pseudoElement);
    }
}
