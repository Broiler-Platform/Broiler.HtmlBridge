using System.Security.Cryptography;
using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): the caller's array is reached
/// through the realm's ordinary property get and set, which is the same <c>[[Get]]</c>/<c>[[Set]]</c>
/// path the engine indexer took, so a typed array's element writes still land where they did.
/// </remarks>
internal static class CryptoBinding
{
    /// <summary>Builds a <c>crypto</c> object exposing <c>getRandomValues</c> and
    /// <c>randomUUID</c>. The same object is shared between <c>window.crypto</c> and the global
    /// <c>crypto</c>.</summary>
    public static JsValue Build(IJsRealm realm)
    {
        var crypto = realm.NewObject();

        realm.DefineValue(crypto, "getRandomValues", realm.NewMethod("getRandomValues", GetRandomValues, 1));
        realm.DefineValue(crypto, "randomUUID", realm.NewMethod("randomUUID", RandomUuid, 0));

        return crypto;
    }

    /// <summary>Fills the caller-supplied integer typed array in place with cryptographically
    /// secure random bytes and returns it (per the Web Crypto contract).</summary>
    private static JsValue GetRandomValues(in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var array = call[0];
        if (array.IsObject)
        {
            var lengthProperty = call.Realm.GetProperty(array, "length");

            // A property that is absent, null or undefined leaves the argument untouched; anything
            // else is coerced the way the engine's own DoubleValue did, so an object or a numeric
            // string still answers a length.
            if (!lengthProperty.IsNullish)
            {
                var length = (int)call.Realm.ToNumber(lengthProperty);
                var buffer = new byte[length];
                RandomNumberGenerator.Fill(buffer);
                for (var i = 0; i < length; i++)
                    call.Realm.SetProperty(array, i.ToString(), JsValue.Number(buffer[i]));
            }
        }

        return array;
    }

    private static JsValue RandomUuid(in JsCall call) => JsValue.String(Guid.NewGuid().ToString());
}
