using System.Runtime.CompilerServices;
using System.Security.Cryptography;

using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Web Crypto's <c>SubtleCrypto</c> — <c>crypto.subtle</c> — and its <c>CryptoKey</c>, for the
/// operations reCAPTCHA calls: <c>digest</c> with SHA-1, SHA-256, SHA-384 and SHA-512, and AES-GCM
/// (<c>importKey</c> of a raw key, <c>encrypt</c>, <c>decrypt</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is here.</b> The bridge's <c>crypto</c> had only <c>getRandomValues</c> and
/// <c>randomUUID</c>. reCAPTCHA's script digests with <c>crypto.subtle</c> (SHA-512 directly, and a
/// proof-of-work loop of up to five million hashes that falls back to hashing in script without it)
/// and encrypts with an AES-GCM key it imports raw, throwing when there is no <c>subtle</c>.
/// </para>
/// <para>
/// <b>Chromium's answers, measured.</b> Every method returns a promise, and every failure — a missing
/// argument and a wrong argument type included — rejects it rather than throwing. Digest names and
/// algorithm names match case-insensitively. A raw AES key is 128 or 256 bits (<c>DataError</c>
/// otherwise; 192 is an <c>OperationError</c>, unsupported), its usages are put in canonical order
/// without repeats, and its <c>algorithm</c> and <c>usages</c> are new objects at every read. AES-GCM's
/// output is the ciphertext followed by the tag (128 bits unless <c>tagLength</c> says otherwise); a
/// tag that does not verify is an <c>OperationError</c> with no message. The messages are Chromium's.
/// </para>
/// <para>
/// <b>Not implemented</b>, and answered with <c>NotSupportedError</c>: every other algorithm, every
/// other key format (<c>jwk</c> included), and <c>generateKey</c>, <c>exportKey</c>, <c>sign</c>,
/// <c>verify</c>, <c>deriveKey</c>, <c>deriveBits</c>, <c>wrapKey</c> and <c>unwrapKey</c>. .NET's
/// AES-GCM takes only a 96-bit IV and a tag of 96 bits or more, so another IV length or a 32- or
/// 64-bit tag is an <c>OperationError</c> here where Chromium computes it.
/// </para>
/// </remarks>
internal static class SubtleCryptoBinding
{
    /// <summary>Web Crypto's key usages, in the order a key reports them.</summary>
    private static readonly string[] UsageOrder = ["encrypt", "decrypt", "sign", "verify", "deriveKey", "deriveBits", "wrapKey", "unwrapKey"];

    /// <summary>The usages an AES-GCM key may have.</summary>
    private static readonly string[] AesGcmUsages = ["encrypt", "decrypt", "wrapKey", "unwrapKey"];

    /// <summary>
    /// The algorithm names Web Crypto registers, as they are spelled. A name outside it is
    /// "Unrecognized"; one in it that is not implemented here is not supported.
    /// </summary>
    private static readonly string[] KnownAlgorithms =
    [
        "AES-CBC", "AES-CTR", "AES-GCM", "AES-KW", "HMAC", "RSASSA-PKCS1-v1_5", "RSA-PSS", "RSA-OAEP",
        "ECDSA", "ECDH", "HKDF", "PBKDF2", "Ed25519", "X25519", "SHA-1", "SHA-256", "SHA-384", "SHA-512",
    ];

    /// <summary>A key's secret and what the page sees of it.</summary>
    private sealed class KeyData(byte[] secret, bool extractable, string[] usages)
    {
        public byte[] Secret { get; } = secret;
        public bool Extractable { get; } = extractable;
        public string[] Usages { get; } = usages;
    }

    /// <summary>The keys this binding made, keyed on the object's identity; weak, so a dropped key goes.</summary>
    private static readonly ConditionalWeakTable<object, KeyData> Keys = new();

    /// <summary>
    /// Defines <c>SubtleCrypto</c> and <c>CryptoKey</c> in <paramref name="realm"/> and answers the
    /// <c>SubtleCrypto</c> object <c>crypto.subtle</c> holds.
    /// </summary>
    internal static JsValue Build(IJsRealm realm)
    {
        var isView = BufferSources.IsViewFunction(realm);
        var keyPrototype = JsValue.Missing;

        var natives = realm.NewObject();
        realm.DefineMethod(natives, "digest", 2, (in call) => Digest(in call, isView));
        realm.DefineMethod(natives, "importKey", 5, (in call) => ImportKey(in call, isView, keyPrototype));
        realm.DefineMethod(natives, "encrypt", 3, (in call) => Crypt(in call, isView, encrypt: true));
        realm.DefineMethod(natives, "decrypt", 3, (in call) => Crypt(in call, isView, encrypt: false));
        realm.DefineMethod(natives, "keyMember", 2, KeyMember);

        var built = realm.Invoke(realm.EvaluateHostScript(InterfaceSource, "polyfill:subtle-crypto"), JsValue.Undefined, [natives]);
        keyPrototype = realm.GetProperty(realm.GetProperty(built, "CryptoKey"), "prototype");
        realm.SetProperty(realm.Global, "SubtleCrypto", realm.GetProperty(built, "SubtleCrypto"));
        realm.SetProperty(realm.Global, "CryptoKey", realm.GetProperty(built, "CryptoKey"));
        return realm.GetProperty(built, "subtle");
    }

    // -------- digest --------

    private static JsValue Digest(in JsCall call, JsValue isView)
    {
        var realm = call.Realm;
        if (!BufferSources.TryGetBytes(realm, call[1], isView, out var data))
            throw NotBufferSource(realm, "digest");

        var name = Normalize(realm, call[0], "digest", ["SHA-1", "SHA-256", "SHA-384", "SHA-512"]);
        byte[] hash = name switch
        {
            "SHA-1" => SHA1.HashData(data),
            "SHA-256" => SHA256.HashData(data),
            "SHA-384" => SHA384.HashData(data),
            _ => SHA512.HashData(data),
        };
        return realm.NewArrayBuffer(hash);
    }

    // -------- importKey --------

    private static JsValue ImportKey(in JsCall call, JsValue isView, JsValue keyPrototype)
    {
        var realm = call.Realm;

        // Web IDL's conversions first, as Chromium's binding makes them: the format enum, then the
        // usages sequence of the KeyUsage enum.
        var format = realm.ToJsString(call[0]);
        if (format is not ("raw" or "spki" or "pkcs8" or "jwk"))
            throw realm.Error(JsErrorKind.TypeError, $"Failed to execute 'importKey' on 'SubtleCrypto': Invalid keyFormat argument: {format}");

        var requested = Usages(realm, call[4]);
        var extractable = realm.ToBoolean(call[3]);
        Normalize(realm, call[2], "importKey", ["AES-GCM"]);

        if (format != "raw")
            throw realm.DomError("NotSupportedError", "Unsupported import key format for algorithm");
        if (!BufferSources.TryGetBytes(realm, call[1], isView, out var secret))
            throw NotBufferSource(realm, "importKey");

        if (requested.Any(usage => !AesGcmUsages.Contains(usage)))
            throw realm.DomError("SyntaxError", "Cannot create a key using the specified key usages.");
        if (secret.Length == 24)
            throw realm.DomError("OperationError", "192-bit AES keys are not supported");
        if (secret.Length is not (16 or 32))
            throw realm.DomError("DataError", "AES key data must be 128 or 256 bits");
        if (requested.Count == 0)
            throw realm.DomError("SyntaxError", "Usages cannot be empty when creating a key.");

        var key = realm.NewObject();
        if (keyPrototype.IsObject)
            realm.SetPrototype(key, keyPrototype);
        Keys.Add(key.ObjectIdentity!, new KeyData(secret, extractable, [.. UsageOrder.Where(requested.Contains)]));
        return key;
    }

    /// <summary>The usages sequence as Web IDL converts it: an iterable of <c>KeyUsage</c> values.</summary>
    private static HashSet<string> Usages(IJsRealm realm, JsValue value)
    {
        if (!value.IsArray)
            throw realm.Error(JsErrorKind.TypeError, "Failed to execute 'importKey' on 'SubtleCrypto': Invalid keyUsages argument");

        var usages = new HashSet<string>(StringComparer.Ordinal);
        var length = (uint)realm.ToNumber(realm.GetProperty(value, "length"));
        for (uint i = 0; i < length; i++)
        {
            var usage = realm.ToJsString(realm.GetIndex(value, i));
            if (!UsageOrder.Contains(usage))
                throw realm.Error(JsErrorKind.TypeError, "Failed to execute 'importKey' on 'SubtleCrypto': Invalid keyUsages argument");
            usages.Add(usage);
        }

        return usages;
    }

    // -------- encrypt / decrypt --------

    private static JsValue Crypt(in JsCall call, JsValue isView, bool encrypt)
    {
        var realm = call.Realm;
        var operation = encrypt ? "encrypt" : "decrypt";

        if (!TryKeyOf(call[1], out var key))
            throw realm.Error(JsErrorKind.TypeError, $"Failed to execute '{operation}' on 'SubtleCrypto': parameter 2 is not of type 'CryptoKey'.");
        if (!BufferSources.TryGetBytes(realm, call[2], isView, out var data))
            throw NotBufferSource(realm, operation);

        var name = Normalize(realm, call[0], operation, ["AES-GCM"], allowKnown: true);

        // AeadParams: iv is required, additionalData and tagLength are not.
        var parameters = call[0].IsObject ? call[0] : JsValue.Missing;
        var ivValue = parameters.IsObject ? realm.GetProperty(parameters, "iv") : JsValue.Missing;
        if (ivValue.IsMissing || ivValue.IsUndefined)
            throw realm.Error(JsErrorKind.TypeError, $"Failed to execute '{operation}' on 'SubtleCrypto': AeadParams: iv: Missing required property");
        if (!BufferSources.TryGetBytes(realm, ivValue, isView, out var iv))
            throw NotBufferSource(realm, operation);

        var associated = Array.Empty<byte>();
        var adValue = realm.GetProperty(parameters, "additionalData");
        if (!adValue.IsMissing && !adValue.IsUndefined && !BufferSources.TryGetBytes(realm, adValue, isView, out associated))
            throw NotBufferSource(realm, operation);

        var tagBits = 128;
        var tagValue = realm.GetProperty(parameters, "tagLength");
        if (!tagValue.IsMissing && !tagValue.IsUndefined)
            tagBits = (int)realm.ToNumber(tagValue);

        // The key's algorithm, then its usages (Web Crypto's encrypt and decrypt steps).
        if (name != "AES-GCM")
            throw realm.DomError("InvalidAccessError", "key.algorithm does not match that of operation");
        if (!key.Usages.Contains(operation))
            throw realm.DomError("InvalidAccessError", "key.usages does not permit this operation");

        if (tagBits is not (32 or 64 or 96 or 104 or 112 or 120 or 128))
            throw realm.DomError("OperationError", "The tag length is invalid: Must be 32, 64, 96, 104, 112, 120, or 128 bits");
        if (iv.Length == 0)
            throw realm.DomError("OperationError", string.Empty);
        if (iv.Length != 12 || tagBits < 96 || !AesGcm.IsSupported)
            throw realm.DomError("OperationError", "AES-GCM is supported with a 96-bit iv and a tag of 96 bits or more only");

        var tagBytes = tagBits / 8;
        using var aes = new AesGcm(key.Secret, tagBytes);
        if (encrypt)
        {
            var output = new byte[data.Length + tagBytes];
            aes.Encrypt(iv, data, output.AsSpan(0, data.Length), output.AsSpan(data.Length), associated);
            return realm.NewArrayBuffer(output);
        }

        if (data.Length < tagBytes)
            throw realm.DomError("OperationError", "The provided data is too small");

        var plaintext = new byte[data.Length - tagBytes];
        try
        {
            aes.Decrypt(iv, data.AsSpan(0, plaintext.Length), data.AsSpan(plaintext.Length), plaintext, associated);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw realm.DomError("OperationError", string.Empty);
        }

        return realm.NewArrayBuffer(plaintext);
    }

    // -------- CryptoKey --------

    /// <summary><c>CryptoKey.prototype</c>'s accessors: <c>keyMember(key, name)</c>.</summary>
    private static JsValue KeyMember(in JsCall call)
    {
        var realm = call.Realm;
        if (!TryKeyOf(call[0], out var key))
            throw realm.Error(JsErrorKind.TypeError, "Illegal invocation");

        switch (realm.ToJsString(call[1]))
        {
            case "type":
                return JsValue.String("secret");
            case "extractable":
                return JsValue.Boolean(key.Extractable);
            case "algorithm":
                var algorithm = realm.NewObject();
                realm.SetProperty(algorithm, "name", JsValue.String("AES-GCM"));
                realm.SetProperty(algorithm, "length", JsValue.Number(key.Secret.Length * 8));
                return algorithm;
            default:
                return realm.NewArray([.. key.Usages.Select(JsValue.String)]);
        }
    }

    private static bool TryKeyOf(JsValue candidate, out KeyData key)
    {
        if (candidate.IsObject && Keys.TryGetValue(candidate.ObjectIdentity!, out var found))
        {
            key = found;
            return true;
        }

        key = null!;
        return false;
    }

    // -------- Algorithms --------

    /// <summary>
    /// Web Crypto's "normalize an algorithm": the name of a string, or of an object's <c>name</c>,
    /// matched case-insensitively against <paramref name="implemented"/>, spelled as registered.
    /// </summary>
    /// <param name="allowKnown">
    /// Answers a registered algorithm that is not implemented, for an operation on a key, which then
    /// fails as not being the key's algorithm (as in Chromium) rather than as unsupported.
    /// </param>
    private static string Normalize(IJsRealm realm, JsValue algorithm, string operation, string[] implemented, bool allowKnown = false)
    {
        string name;
        if (algorithm.IsObject)
        {
            var member = realm.GetProperty(algorithm, "name");
            if (!member.IsString)
                throw realm.Error(JsErrorKind.TypeError, $"Failed to execute '{operation}' on 'SubtleCrypto': Algorithm: name: Missing or not a string");
            name = member.AsString!;
        }
        else
        {
            name = realm.ToJsString(algorithm);
        }

        if (implemented.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } found)
            return found;

        if (KnownAlgorithms.FirstOrDefault(candidate => candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } known)
        {
            if (allowKnown)
                return known;
            throw realm.DomError("NotSupportedError", $"Failed to execute '{operation}' on 'SubtleCrypto': {known} is not supported");
        }

        throw realm.DomError("NotSupportedError", $"Failed to execute '{operation}' on 'SubtleCrypto': Algorithm: Unrecognized name");
    }

    private static Exception NotBufferSource(IJsRealm realm, string operation) =>
        realm.Error(JsErrorKind.TypeError, $"Failed to execute '{operation}' on 'SubtleCrypto': The provided value is not of type '(ArrayBuffer or ArrayBufferView)'.");

    /// <summary>
    /// The two interfaces, built in script so that each method is a promise-returning function as Web
    /// IDL makes it: a missing argument and anything the native half throws reject the promise.
    /// </summary>
    private const string InterfaceSource = """
        (function (natives) {
            var Resolve = Promise.resolve.bind(Promise);
            var Reject = Promise.reject.bind(Promise);

            function SubtleCrypto() {
                throw new TypeError("Failed to construct 'SubtleCrypto': Illegal constructor");
            }
            function CryptoKey() {
                throw new TypeError("Failed to construct 'CryptoKey': Illegal constructor");
            }

            function define(target, name, value) {
                Object.defineProperty(target, name, { value: value, writable: true, enumerable: true, configurable: true });
            }

            function method(name, required, native) {
                var body = function () {
                    if (arguments.length < required)
                        return Reject(new TypeError("Failed to execute '" + name + "' on 'SubtleCrypto': " + required +
                            " argument" + (required === 1 ? '' : 's') + " required, but only " + arguments.length + " present."));
                    if (!native)
                        return Reject(new DOMException("Failed to execute '" + name + "' on 'SubtleCrypto': The operation is not supported.", 'NotSupportedError'));
                    try {
                        return Resolve(native.apply(natives, arguments));
                    } catch (e) {
                        return Reject(e);
                    }
                };
                Object.defineProperty(body, 'name', { value: name });
                Object.defineProperty(body, 'length', { value: required });
                define(SubtleCrypto.prototype, name, body);
            }

            method('encrypt', 3, natives.encrypt);
            method('decrypt', 3, natives.decrypt);
            method('sign', 3, null);
            method('verify', 4, null);
            method('digest', 2, natives.digest);
            method('generateKey', 3, null);
            method('deriveKey', 5, null);
            method('deriveBits', 3, null);
            method('importKey', 5, natives.importKey);
            method('exportKey', 2, null);
            method('wrapKey', 4, null);
            method('unwrapKey', 7, null);

            ['type', 'extractable', 'algorithm', 'usages'].forEach(function (member) {
                Object.defineProperty(CryptoKey.prototype, member, {
                    get: function () { return natives.keyMember(this, member); },
                    enumerable: true,
                    configurable: true
                });
            });

            Object.defineProperty(SubtleCrypto.prototype, Symbol.toStringTag, { value: 'SubtleCrypto', configurable: true });
            Object.defineProperty(CryptoKey.prototype, Symbol.toStringTag, { value: 'CryptoKey', configurable: true });

            return { SubtleCrypto: SubtleCrypto, CryptoKey: CryptoKey, subtle: Object.create(SubtleCrypto.prototype) };
        })
        """;
}
