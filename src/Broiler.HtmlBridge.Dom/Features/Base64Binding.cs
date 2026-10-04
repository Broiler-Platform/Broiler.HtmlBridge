using Broiler.JSeal;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>btoa</c> and <c>atob</c> — the base64 pair on <c>WindowOrWorkerGlobalScope</c>
/// (HTML §8.3), which the bridge did not provide at all. An unqualified <c>atob(…)</c> was a
/// <c>ReferenceError</c>, and that aborts the whole script rather than the one call.
/// <para>
/// Both work on <em>binary strings</em>, not text: <c>btoa</c> takes a string whose code points are
/// all ≤ U+00FF and treats each as one byte, and <c>atob</c> returns a string built the same way.
/// That is why neither is a UTF-8 round trip and why <c>btoa</c> throws on ordinary non-Latin text
/// — a mistake worth failing loudly rather than mangling, since the alternative silently corrupts
/// the bytes a page is trying to move.
/// </para>
/// </summary>
internal static class Base64Binding
{
    /// <summary>
    /// <c>btoa(data)</c> — base64-encodes a binary string. Throws <c>InvalidCharacterError</c> for a
    /// code point above U+00FF, which cannot be one byte.
    /// </summary>
    /// <remarks>
    /// The argument is coerced with the realm's <c>ToString</c>, because <c>btoa(data)</c> takes a
    /// WebIDL <c>DOMString</c> and a page passing a number or an object is entitled to the language's
    /// conversion — which can run a <c>toString</c> the page wrote. The <c>DOMException</c> is minted
    /// through the realm rather than by reaching for the <c>DOMException</c> global directly; it is
    /// the same object built the same way, from the same constructor, and it is returned to be
    /// thrown so the compiler can see this path ends.
    /// </remarks>
    internal static JsValue Btoa(in JsCall call)
    {
        var input = call.Length > 0 ? call.Realm.ToJsString(call[0]) : "undefined";
        var bytes = new byte[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            if (input[i] > 0xFF)
            {
                throw call.Realm.DomError("InvalidCharacterError",
                    "The string to be encoded contains characters outside of the Latin1 range.");
            }

            bytes[i] = (byte)input[i];
        }

        return JsValue.String(Convert.ToBase64String(bytes));
    }

    /// <summary>
    /// <c>atob(data)</c> — decodes base64 to a binary string, by Infra's <em>forgiving-base64
    /// decode</em> (<see cref="ForgivingBase64"/>), which <c>data:</c> URLs share.
    /// Throws <c>InvalidCharacterError</c> when the input cannot be decoded.
    /// </summary>
    internal static JsValue Atob(in JsCall call)
    {
        var input = call.Length > 0 ? call.Realm.ToJsString(call[0]) : "undefined";
        if (!ForgivingBase64.TryDecode(input, out var bytes))
        {
            throw call.Realm.DomError("InvalidCharacterError",
                "The string to be decoded is not correctly encoded.");
        }

        // Each byte becomes one code unit, which is what makes the result a binary string rather
        // than decoded text — the inverse of what Btoa consumed.
        return JsValue.String(string.Create(bytes.Length, bytes, static (chars, source) =>
        {
            for (var i = 0; i < source.Length; i++)
                chars[i] = (char)source[i];
        }));
    }
}
