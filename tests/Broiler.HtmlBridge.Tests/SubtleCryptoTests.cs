using Broiler.HtmlBridge;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>crypto.subtle</c>: <c>digest</c> and AES-GCM's <c>importKey</c>, <c>encrypt</c> and
/// <c>decrypt</c>, on a page and in a worker. Every expectation is Chromium's answer, measured with the
/// same scripts — the hashes, the ciphertexts, the key's description and each error's name and message.
/// </summary>
/// <remarks>
/// The bridge's <c>crypto</c> had no <c>subtle</c>. reCAPTCHA's script digests with it — SHA-512, and a
/// proof-of-work loop of up to five million hashes that falls back to hashing in script without it —
/// and encrypts with an AES-GCM key it imports raw, throwing when <c>subtle</c> is not there.
/// </remarks>
public class SubtleCryptoTests
{
    private const string Secure = "https://example.test/page";

    /// <summary>
    /// Runs <paramref name="script"/> on a page at <paramref name="url"/> and answers what it passed to
    /// <c>done</c>. <c>hex(buffer)</c> spells a buffer's bytes, <c>fail(e)</c> an error as
    /// <c>Name: message</c>, and <c>bytes(n, from)</c> makes <c>n</c> bytes counting up from <c>from</c>.
    /// </summary>
    private static string Run(string script, string url = Secure)
    {
        const string helpers =
            "function done(v) { document.getElementById('out').textContent = String(v); }" +
            "function hex(buffer) { var b = new Uint8Array(buffer), out = '';" +
            " for (var i = 0; i < b.length; i++) out += ('0' + b[i].toString(16)).slice(-2); return out; }" +
            "function fail(e) { return e.name + ': ' + e.message; }" +
            "function bytes(n, from) { var b = new Uint8Array(n); for (var i = 0; i < n; i++) b[i] = from + i; return b; }";
        var rendered = new ScriptEngine().Execute([helpers, script],
            "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div></body></html>", url);
        Assert.NotNull(rendered);
        return PageProbe.OutOf(rendered!, decode: true);
    }

    /// <summary>
    /// A script that runs each <c>[label, () =&gt; promise]</c> step in turn and passes
    /// <c>label=result</c> lines to <c>done</c>: the hex of a buffer, or the rejection as <c>fail</c>
    /// spells it.
    /// </summary>
    private static string Steps(string steps) =>
        "var steps = [" + steps + "], lines = [], chain = Promise.resolve();" +
        "steps.forEach(function (step) { chain = chain.then(function () {" +
        " var p; try { p = step[1](); } catch (e) { lines.push(step[0] + '=threw ' + fail(e)); return; }" +
        " if (!(p instanceof Promise)) { lines.push(step[0] + '=not a promise'); return; }" +
        " return p.then(function (v) { lines.push(step[0] + '=' + (v instanceof ArrayBuffer ? hex(v) : String(v))); }," +
        " function (e) { lines.push(step[0] + '=' + fail(e)); }); }); });" +
        "chain.then(function () { done(lines.join('\\n')); });";

    private static string[] Lines(string result) => result.Split('\n');

    /// <summary>
    /// <c>crypto.subtle</c> is a <c>SubtleCrypto</c> — the same object at every read — in a secure
    /// context: an HTTPS page, or an HTTP one on a loopback address or a localhost name. On any other
    /// HTTP page there is none.
    /// </summary>
    [Fact]
    public void SubtleIsThereInASecureContextOnly()
    {
        const string probe =
            "done([typeof crypto.subtle, Object.prototype.toString.call(crypto.subtle)," +
            " crypto.subtle instanceof SubtleCrypto, crypto.subtle === crypto.subtle].join('|'));";

        Assert.Equal("object|[object SubtleCrypto]|true|true", Run(probe));
        Assert.Equal("object|[object SubtleCrypto]|true|true", Run(probe, "http://127.0.0.1:8080/page"));
        Assert.Equal("object|[object SubtleCrypto]|true|true", Run(probe, "http://localhost:8080/page"));
        Assert.Equal("undefined|[object Undefined]|false|true", Run(probe, "http://example.test/page"));
    }

    /// <summary>
    /// <c>digest</c> hashes a buffer or the span a view covers with SHA-1, SHA-256, SHA-384 or SHA-512,
    /// whose names match in any case, given as a string or as an object's <c>name</c>.
    /// </summary>
    [Fact]
    public void DigestHashesWithTheShaFamily()
    {
        var result = Run(Steps(
            "['sha1', function () { return crypto.subtle.digest('SHA-1', new TextEncoder().encode('abc')); }]," +
            "['sha256', function () { return crypto.subtle.digest('SHA-256', new TextEncoder().encode('abc')); }]," +
            "['sha384', function () { return crypto.subtle.digest({ name: 'sha-384' }, new Uint8Array(0)); }]," +
            "['sha512', function () { return crypto.subtle.digest('Sha-512', new TextEncoder().encode('abc').buffer); }]," +
            "['view', function () { return crypto.subtle.digest('SHA-256', new Uint8Array([0, 97, 98, 99, 0]).subarray(1, 4)); }]"));

        Assert.Equal(
            [
                "sha1=a9993e364706816aba3e25717850c26c9cd0d89d",
                "sha256=ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                "sha384=38b060a751ac96384cd9327eb1b1e36a21fdb71114be07434c0cc7bf63f6e1da274edebfe76f65fbd51ad2f14898b95b",
                "sha512=ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
                "view=ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            ],
            Lines(result));
    }

    /// <summary>
    /// What <c>digest</c> refuses it refuses by rejecting its promise, never by throwing: an
    /// unrecognized algorithm, data that is not a buffer, missing arguments, an algorithm with no name.
    /// </summary>
    [Fact]
    public void DigestRejectsWhatItCannotHash()
    {
        var result = Run(Steps(
            "['alg', function () { return crypto.subtle.digest('SHA-999', new Uint8Array(1)); }]," +
            "['data', function () { return crypto.subtle.digest('SHA-256', 'abc'); }]," +
            "['none', function () { return crypto.subtle.digest(); }]," +
            "['noname', function () { return crypto.subtle.digest({}, new Uint8Array(1)); }]"));

        Assert.Equal(
            [
                "alg=NotSupportedError: Failed to execute 'digest' on 'SubtleCrypto': Algorithm: Unrecognized name",
                "data=TypeError: Failed to execute 'digest' on 'SubtleCrypto': The provided value is not of type '(ArrayBuffer or ArrayBufferView)'.",
                "none=TypeError: Failed to execute 'digest' on 'SubtleCrypto': 2 arguments required, but only 0 present.",
                "noname=TypeError: Failed to execute 'digest' on 'SubtleCrypto': Algorithm: name: Missing or not a string",
            ],
            Lines(result));
    }

    /// <summary>
    /// A raw AES-GCM key is a <c>CryptoKey</c>: secret, its length in bits, its usages in canonical order
    /// without repeats, and a new <c>algorithm</c> object and <c>usages</c> array at every read.
    /// </summary>
    [Fact]
    public void ARawAesGcmKeyIsACryptoKey()
    {
        var result = Run(
            "crypto.subtle.importKey('raw', bytes(32, 0), { name: 'AES-GCM', length: 32 }, false, ['decrypt', 'encrypt', 'encrypt'])" +
            ".then(function (key) {" +
            " var a = key.algorithm; a.name = 'X';" +
            " done([JSON.stringify({ type: key.type, extractable: key.extractable, algorithm: key.algorithm, usages: key.usages })," +
            "  Object.prototype.toString.call(key), key instanceof CryptoKey, key.algorithm === key.algorithm, key.usages === key.usages," +
            "  Object.getOwnPropertyNames(key).length].join('|')); }, function (e) { done(fail(e)); });");

        Assert.Equal(
            "{\"type\":\"secret\",\"extractable\":false,\"algorithm\":{\"name\":\"AES-GCM\",\"length\":256},\"usages\":[\"encrypt\",\"decrypt\"]}" +
            "|[object CryptoKey]|true|false|false|0",
            result);
    }

    /// <summary>
    /// AES-GCM encrypts to the ciphertext followed by the tag — 128 bits, or the <c>tagLength</c> asked
    /// for — over the additional data, and decrypts it back; a changed tag, or data shorter than a tag,
    /// is an <c>OperationError</c>. The ciphertexts are Chromium's for the same key, IV and text.
    /// </summary>
    [Fact]
    public void AesGcmEncryptsAndDecryptsAsChromiumDoes()
    {
        var result = Run(
            "var key, key16, iv = bytes(12, 100), text = new TextEncoder().encode('hello'), sealed;" +
            "crypto.subtle.importKey('raw', bytes(32, 0), 'AES-GCM', false, ['encrypt', 'decrypt'])" +
            ".then(function (k) { key = k; return crypto.subtle.importKey('raw', bytes(16, 0), 'AES-GCM', true, ['encrypt']); })" +
            ".then(function (k) { key16 = k;" + Steps(
                "['plain', function () { return crypto.subtle.encrypt({ name: 'AES-GCM', iv: iv, additionalData: new Uint8Array(0) }, key, text)" +
                "  .then(function (c) { sealed = c; return c; }); }]," +
                "['ad96', function () { return crypto.subtle.encrypt({ name: 'aes-gcm', iv: iv, additionalData: new TextEncoder().encode('ad'), tagLength: 96 }, key, text); }]," +
                "['key16', function () { return crypto.subtle.encrypt({ name: 'AES-GCM', iv: iv }, key16, text); }]," +
                "['open', function () { return crypto.subtle.decrypt({ name: 'AES-GCM', iv: iv }, key, sealed)" +
                "  .then(function (p) { return new TextDecoder().decode(p); }); }]," +
                "['tampered', function () { var t = new Uint8Array(sealed); t[t.length - 1] ^= 1;" +
                "  return crypto.subtle.decrypt({ name: 'AES-GCM', iv: iv }, key, t); }]," +
                "['short', function () { return crypto.subtle.decrypt({ name: 'AES-GCM', iv: iv }, key, new Uint8Array(4)); }]") +
            "});");

        Assert.Equal(
            [
                "plain=207eb20a16c972e0b32a51faa93e00c2defb3af145",
                "ad96=207eb20a16d6c442ee179ec4e3b510c101",
                "key16=720722c9b4662bc3362474f313c904d1e1d23f47b3",
                "open=hello",
                "tampered=OperationError: ",
                "short=OperationError: The provided data is too small",
            ],
            Lines(result));
    }

    /// <summary>
    /// What <c>importKey</c>, <c>encrypt</c> and <c>decrypt</c> refuse, each with Chromium's error and
    /// message: a key of another length, usages a key cannot have or none, another format or
    /// algorithm, a missing IV, a tag length GCM does not have, something that is not a key, a key of
    /// another algorithm or without the usage.
    /// </summary>
    [Fact]
    public void KeysAndCiphersRejectAsChromiumDoes()
    {
        var result = Run(
            "var key, key16, iv = bytes(12, 100), raw = bytes(32, 0), x = new Uint8Array([120]);" +
            "crypto.subtle.importKey('raw', raw, 'AES-GCM', false, ['encrypt', 'decrypt'])" +
            ".then(function (k) { key = k; return crypto.subtle.importKey('raw', bytes(16, 0), 'AES-GCM', true, ['encrypt']); })" +
            ".then(function (k) { key16 = k;" + Steps(
                "['len20', function () { return crypto.subtle.importKey('raw', new Uint8Array(20), 'AES-GCM', false, ['encrypt']); }]," +
                "['len24', function () { return crypto.subtle.importKey('raw', new Uint8Array(24), 'AES-GCM', false, ['encrypt']); }]," +
                "['sign', function () { return crypto.subtle.importKey('raw', raw, 'AES-GCM', false, ['sign']); }]," +
                "['empty', function () { return crypto.subtle.importKey('raw', raw, 'AES-GCM', false, []); }]," +
                "['spki', function () { return crypto.subtle.importKey('spki', raw, 'AES-GCM', false, ['encrypt']); }]," +
                "['format', function () { return crypto.subtle.importKey('xyz', raw, 'AES-GCM', false, ['encrypt']); }]," +
                "['alg', function () { return crypto.subtle.importKey('raw', raw, 'FOO', false, ['encrypt']); }]," +
                "['usage', function () { return crypto.subtle.importKey('raw', raw, 'AES-GCM', false, ['bogus']); }]," +
                "['wrap', function () { return crypto.subtle.importKey('raw', raw, 'AES-GCM', true, ['wrapKey', 'unwrapKey', 'encrypt'])" +
                "  .then(function (k) { return k.usages.join(','); }); }]," +
                "['noiv', function () { return crypto.subtle.encrypt({ name: 'AES-GCM' }, key, x); }]," +
                "['stralg', function () { return crypto.subtle.encrypt('AES-GCM', key, x); }]," +
                "['iv0', function () { return crypto.subtle.encrypt({ name: 'AES-GCM', iv: new Uint8Array(0) }, key, x); }]," +
                "['tag100', function () { return crypto.subtle.encrypt({ name: 'AES-GCM', iv: iv, tagLength: 100 }, key, x); }]," +
                "['notkey', function () { return crypto.subtle.encrypt({ name: 'AES-GCM', iv: iv }, {}, x); }]," +
                "['cbc', function () { return crypto.subtle.encrypt({ name: 'AES-CBC', iv: new Uint8Array(16) }, key, x); }]," +
                "['nousage', function () { return crypto.subtle.decrypt({ name: 'AES-GCM', iv: iv }, key16, x); }]," +
                "['hmac', function () { return crypto.subtle.sign('HMAC', key, x); }]") +
            "});");

        Assert.Equal(
            [
                "len20=DataError: AES key data must be 128 or 256 bits",
                "len24=OperationError: 192-bit AES keys are not supported",
                "sign=SyntaxError: Cannot create a key using the specified key usages.",
                "empty=SyntaxError: Usages cannot be empty when creating a key.",
                "spki=NotSupportedError: Unsupported import key format for algorithm",
                "format=TypeError: Failed to execute 'importKey' on 'SubtleCrypto': Invalid keyFormat argument: xyz",
                "alg=NotSupportedError: Failed to execute 'importKey' on 'SubtleCrypto': Algorithm: Unrecognized name",
                "usage=TypeError: Failed to execute 'importKey' on 'SubtleCrypto': Invalid keyUsages argument",
                "wrap=encrypt,wrapKey,unwrapKey",
                "noiv=TypeError: Failed to execute 'encrypt' on 'SubtleCrypto': AeadParams: iv: Missing required property",
                "stralg=TypeError: Failed to execute 'encrypt' on 'SubtleCrypto': AeadParams: iv: Missing required property",
                "iv0=OperationError: ",
                "tag100=OperationError: The tag length is invalid: Must be 32, 64, 96, 104, 112, 120, or 128 bits",
                "notkey=TypeError: Failed to execute 'encrypt' on 'SubtleCrypto': parameter 2 is not of type 'CryptoKey'.",
                "cbc=InvalidAccessError: key.algorithm does not match that of operation",
                "nousage=InvalidAccessError: key.usages does not permit this operation",
                "hmac=NotSupportedError: Failed to execute 'sign' on 'SubtleCrypto': The operation is not supported.",
            ],
            Lines(result));
    }

    /// <summary><c>SubtleCrypto</c> and <c>CryptoKey</c> are interfaces a page cannot construct.</summary>
    [Fact]
    public void NeitherInterfaceCanBeConstructed()
    {
        var result = Run(
            "var out = [];" +
            "try { new CryptoKey(); out.push('made'); } catch (e) { out.push(fail(e)); }" +
            "try { new SubtleCrypto(); out.push('made'); } catch (e) { out.push(fail(e)); }" +
            "done(out.join('|'));");

        Assert.Equal(
            "TypeError: Failed to construct 'CryptoKey': Illegal constructor|TypeError: Failed to construct 'SubtleCrypto': Illegal constructor",
            result);
    }

    /// <summary>
    /// A worker a secure page starts has <c>crypto.subtle</c> too — reCAPTCHA's checkbox frame hands
    /// work to one — and digests in it as the page does.
    /// </summary>
    [Fact]
    public void AWorkerOfASecurePageDigestsToo()
    {
        using var server = new LoopbackCookieServer();
        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));
        const string worker =
            "onmessage = function () { crypto.subtle.digest('SHA-256', new TextEncoder().encode('abc')).then(function (h) {" +
            " var b = new Uint8Array(h), s = ''; for (var i = 0; i < b.length; i++) s += ('0' + b[i].toString(16)).slice(-2);" +
            " postMessage(typeof crypto.subtle + ' ' + s); }, function (e) { postMessage('rejected ' + e.name); }); };";
        using var session = engine.ExecuteInteractive(
            [
                "var out = document.getElementById('out');" +
                $"var worker = new Worker(URL.createObjectURL(new Blob([{System.Text.Json.JsonSerializer.Serialize(worker)}], {{ type: 'text/javascript' }})));" +
                "worker.onmessage = function (e) { out.textContent = String(e.data); };" +
                "worker.onerror = function () { out.textContent = 'error'; };" +
                "worker.postMessage('go');",
            ],
            [],
            "<html><head></head><body><div id=\"out\">waiting</div></body></html>",
            server.LocalhostUrl("/page"));

        Assert.Equal(
            "object ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            PageProbe.OutOf(session!.SettleLoadWindow(), decode: true));
    }
}
