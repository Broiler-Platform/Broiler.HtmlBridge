using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Web IDL's <c>BufferSource</c>: an <c>ArrayBuffer</c>, or a view of one — a typed array or a
/// <c>DataView</c> — whose bytes are the span it covers. What a fetch body and a Web Crypto operation
/// take their bytes from.
/// </summary>
internal static class BufferSources
{
    /// <summary>The realm's own <c>ArrayBuffer.isView</c>, to read before a page's script runs.</summary>
    internal static JsValue IsViewFunction(IJsRealm realm) =>
        realm.GetProperty(realm.GetProperty(realm.Global, "ArrayBuffer"), "isView");

    /// <summary>
    /// The bytes of <paramref name="value"/> when it is a <c>BufferSource</c>: an <c>ArrayBuffer</c>'s
    /// own, or the span a view of one covers, copied.
    /// </summary>
    /// <param name="isView">
    /// The realm's <c>ArrayBuffer.isView</c> (<see cref="IsViewFunction"/>). A view is recognised by it,
    /// not by its having a <c>buffer</c>: an object of the page's that carries one is not a view.
    /// </param>
    internal static bool TryGetBytes(IJsRealm realm, JsValue value, JsValue isView, out byte[] bytes)
    {
        bytes = [];
        if (!value.IsObject)
            return false;

        if (realm.TryGetArrayBufferBytes(value, out var whole))
        {
            bytes = whole;
            return true;
        }

        if (!isView.IsFunction || !realm.Invoke(isView, JsValue.Undefined, [value]).AsBoolean)
            return false;

        if (!realm.TryGetArrayBufferBytes(realm.GetProperty(value, "buffer"), out var source))
            return false;

        var offset = (int)Math.Clamp(realm.ToNumber(realm.GetProperty(value, "byteOffset")), 0, source.Length);
        var length = (int)Math.Clamp(realm.ToNumber(realm.GetProperty(value, "byteLength")), 0, source.Length - offset);
        bytes = source.AsSpan(offset, length).ToArray();
        return true;
    }
}
