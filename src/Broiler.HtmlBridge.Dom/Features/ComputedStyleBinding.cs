using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The computed-style reads, co-located as an HtmlBridge feature module: the CSSOM entry point
/// <c>window.getComputedStyle(element, pseudoElement?)</c> (which resolves an element's used-value style
/// declaration), and the <c>&lt;img&gt;.width</c>/<c>&lt;img&gt;.height</c> IDL getters, which report the
/// element's used (rendered) dimension by reading it out of the same computed-style object, falling back to
/// the content attribute and then <c>0</c>. Both reach the used-value engine through the narrow
/// <see cref="IComputedStyleHost"/> contract; the content-attribute fallback and CSS-length parse use the
/// bridge's neutral <c>internal static</c> <c>TryGetAttribute</c>/<c>ParseCssLengthToPixels</c> helpers
/// directly.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's, so the bodies name no engine type. <c>getComputedStyle</c>
/// reads its own call frame — its installer mints it through the realm — and the coercion of the
/// pseudo-element argument is the realm's <c>ToJsString</c>, the observable ECMAScript
/// <c>ToString</c>. <c>&lt;img&gt;.width</c>/<c>.height</c>
/// need no frame: <c>DomBridge/NodeInterfaces.cs</c> mints that accessor pair through the realm and
/// its getter calls <c>GetUsedDimension</c> with the element and dimension name.
/// </remarks>
internal static class ComputedStyleBinding
{
    /// <summary>
    /// <c>window.getComputedStyle(element, pseudoElement)</c> — the used-value declaration for
    /// <paramref name="target"/>'s element, or an empty object when the call named no element at all.
    /// </summary>
    internal static JsValue GetComputedStyle(IComputedStyleHost host, JsValue target, string? pseudoElement)
    {
        var el = host.FindElement(target);
        return host.BuildComputedStyle(el, pseudoElement);
    }

    /// <summary>
    /// The <c>&lt;img&gt;.width</c> / <c>&lt;img&gt;.height</c> IDL getter — the used (rendered)
    /// dimension read out of computed style, falling back to the raw content attribute, then <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Both reads test <see cref="double.IsFinite(double)"/> rather than <c>!IsNaN</c>, which is
    /// what "not a dimension" has to mean here. <c>NumberStyles.Float</c> accepts <c>Infinity</c>
    /// and <c>NaN</c> by name and overflows a double on an exponent — or on a long enough run of
    /// digits — with no symbol in the value at all, so <c>&lt;img style="width: 1e400px"&gt;</c> and
    /// <c>&lt;img width="1e400"&gt;</c> both answered <c>img.width === Infinity</c>.
    /// <para>
    /// Refusing at each read rather than at one exit is what keeps the documented chain: a
    /// computed value this getter cannot represent falls through to the content attribute exactly
    /// as an unreadable one does, and an attribute it cannot represent falls through to <c>0</c>.
    /// Those are not substitutions invented by the guard — they are the fallbacks the getter
    /// already had, and the only ones it has.
    /// </para>
    /// <para>
    /// The attribute read goes through <c>TryParseFiniteScalar</c> for its <em>culture</em> as much
    /// as its finiteness. It used to call the parameterless <c>double.TryParse</c>, which reads the
    /// machine's current culture: on a German one the group separator is <c>.</c> and the decimal
    /// separator is <c>,</c>, so <c>width="1.234"</c> answered <c>1234</c> and <c>width="1,5"</c>
    /// answered <c>1.5</c>. A content attribute is not a localised number — HTML parses it with no
    /// culture at all — so the same document answered differently depending on where it was opened.
    /// </para>
    /// <para>
    /// This still reads the attribute as a <em>number</em> rather than by HTML's rules for parsing
    /// non-negative integers, which would take the leading digit run and stop: <c>width="1.5"</c> is
    /// <c>1</c> to a browser and <c>1.5</c> here, and a negative value should be ignored outright
    /// rather than used. That is a separate question from the culture, and a larger one, so it is
    /// left recorded rather than folded in here.
    /// </para>
    /// </remarks>
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
                var px = DomBridgeUtils.ParseCssLengthToPixels(cssStr);
                if (double.IsFinite(px))
                    return JsValue.Number(px);
            }
        }

        // Fallback: HTML attribute. Read through the shared scalar helper, which fixes the culture
        // as well as the finiteness — see the remark below for why the culture matters here.
        if (DomBridgeUtils.TryGetAttribute(element, dimName, out var attrVal) &&
            DomBridgeUtils.TryParseFiniteScalar(attrVal, out var attrNum))
        {
            return JsValue.Number(attrNum);
        }

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
