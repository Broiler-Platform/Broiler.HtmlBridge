using System.Security.Cryptography;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Number;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The Web Crypto <c>crypto</c> object — the <c>getRandomValues</c> and <c>randomUUID</c> subset —
/// co-located as an HtmlBridge feature module (Phase 3). It fills a caller-supplied typed array
/// with random bytes and mints v4-style UUIDs, touching no bridge instance state, so — like
/// <c>ConsoleBinding</c> / <c>ClassListBinding</c> — it is a pure static class with no host
/// contract. Previously the <c>crypto</c> object was built inline in the bridge's
/// <c>RegisterSecurityAndConstructorPolyfills</c> and its <c>getRandomValues</c> callback lived in
/// the shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class CryptoBinding
{
    /// <summary>Builds a <c>crypto</c> object exposing <c>getRandomValues</c> and
    /// <c>randomUUID</c>. The same object is shared between <c>window.crypto</c> and the global
    /// <c>crypto</c>.</summary>
    public static JSObject Build()
    {
        var crypto = new JSObject();

        crypto.FastAddValue(
            "getRandomValues",
            new DomFunction(GetRandomValues, "getRandomValues", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        crypto.FastAddValue(
            "randomUUID",
            new DomFunction(RandomUuid, "randomUUID", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return crypto;
    }

    /// <summary>Fills the caller-supplied integer typed array in place with cryptographically
    /// secure random bytes and returns it (per the Web Crypto contract).</summary>
    private static JSValue GetRandomValues(in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var arr = a[0];
        if (arr is JSObject arrObj)
        {
            var lengthProp = arrObj[(KeyString)"length"];
            if (lengthProp != null && !lengthProp.IsUndefined && !lengthProp.IsNull)
            {
                var len = (int)lengthProp.DoubleValue;
                var buffer = new byte[len];
                RandomNumberGenerator.Fill(buffer);
                for (var i = 0; i < len; i++)
                    arrObj[(KeyString)i.ToString()] = new JSNumber(buffer[i]);
            }
        }

        return arr;
    }

    private static JSValue RandomUuid(in Arguments a) => new JSString(Guid.NewGuid().ToString());
}
