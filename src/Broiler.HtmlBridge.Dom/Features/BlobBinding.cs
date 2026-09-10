using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The File API's data surfaces — <c>Blob</c>, <c>File</c>, and the <c>URL.createObjectURL</c> /
/// <c>revokeObjectURL</c> pair — as real interfaces over real bytes.
/// </summary>
/// <remarks>
/// <para>
/// None of them existed, so the bare name was a <c>ReferenceError</c>: the kind that aborts the
/// script rather than the statement. <c>Blob</c> in particular is reached by ordinary pages, not only
/// by file-upload code — it is how a page builds a downloadable payload
/// (<c>URL.createObjectURL(new Blob([csv], {type: 'text/csv'}))</c>), how it posts binary through
/// <c>fetch</c>, and what <c>response.blob()</c> is supposed to hand back.
/// </para>
/// <para>
/// <b>This replaces a shape-only stub as well as filling an absence.</b> <c>response.blob()</c>
/// already answered — with a plain object carrying <c>size</c>, <c>type</c>, <c>text()</c> and
/// <c>arrayBuffer()</c> and nothing else, so <c>constructor.name</c> was <c>"Object"</c>, there was no
/// <c>slice</c>, and the object could not be handed to anything that checks what it is. Now that
/// <c>Blob</c> exists, that path mints one.
/// </para>
/// <para>
/// A blob's bytes live in a weak table keyed by the object, the same shape <c>Range</c> and
/// <c>Selection</c> use, so the members are on the prototypes and an instance has no own properties.
/// </para>
/// <para>
/// <b><c>stream()</c> is in, and it is what made <c>ReadableStream</c> real.</b> It was left out
/// because it returns one, and this engine had only a partial stream — the object
/// <c>response.body</c> handed back — that a second copy should not have been written against. That
/// decision has been taken the other way: there is one <c>ReadableStream</c> now, a page's own
/// <c>new ReadableStream</c> builds the same interface, and both <c>blob.stream()</c> and a fetch
/// body hand back one of those rather than a look-alike.
/// </para>
/// <para>
/// Every expectation is Chromium's measured answer. Three are worth naming because reasoning gets
/// them wrong: the parts argument must be an actual sequence, so <c>new Blob('abc')</c> is a
/// <c>TypeError</c> rather than a three-byte blob; a <c>type</c> carrying a character outside
/// U+0020–U+007E is discarded entirely rather than kept or escaped; and <c>slice</c> without a
/// content type gives the result an <em>empty</em> type rather than inheriting the source's.
/// </para>
/// <para>
/// <b>ONE engine type survives the JSEAL migration here, and it is named where it occurs.</b> The
/// blob store is keyed on the engine object a handle carries, because <see cref="JsValue"/> is a
/// struct and a weak table needs a reference to key on — object identity is the whole of what makes
/// a blob a blob, and JSEAL exposes no identity handle a table can hold. That is the remaining gap,
/// and it is a cost of the value design rather than a missing member.
/// </para>
/// <para>
/// <b>The binary-data gap this file specified is closed, and the specification is worth keeping
/// because it is why the contract has the shape it has.</b> It asked for three operations, not one:
/// <em>mint</em> a buffer over a byte array; <em>test</em> whether a handle is one, because
/// <c>new Blob([buf])</c> has to distinguish a buffer from an object it must stringify and there is
/// no JS-visible property that answers it; and <em>read</em> the bytes back out. A view — a typed
/// array or a <c>DataView</c> — needed no contract of its own, because <see cref="PartBytes"/>
/// reaches its <c>buffer</c>, <c>byteOffset</c> and <c>byteLength</c> through the ordinary property
/// reads a script would use, and only the buffer at the end of that chain was untypeable.
/// <see cref="IJsValues.NewArrayBuffer"/> is the mint; <see cref="IJsValues.TryGetArrayBufferBytes"/>
/// is the test and the read together, because they are one question to an engine and every call site
/// asked them as one. <see cref="PartBytes"/> below is that algorithm unchanged.
/// </para>
/// </remarks>
internal sealed class BlobBinding
{
    private JsValue _blobPrototype;
    private JsValue _filePrototype;

    /// <summary>The bytes and metadata behind each blob object. Weak, so a blob a page has dropped is
    /// not kept alive by this table.</summary>
    /// <remarks>
    /// Keyed on <see cref="JsValue.ObjectIdentity"/> - the reference the handle carries, which a
    /// provider is already required to make canonical per object because handle equality is defined
    /// by it. This table used to name the engine's own type, on the reasoning that a struct cannot be
    /// a weak-table key; the struct is not the key.
    /// </remarks>
    private readonly ConditionalWeakTable<object, BlobData> _blobs = new();

    /// <summary>
    /// The identity the blob store keys on: the reference the handle carries.
    /// </summary>
    /// <remarks>
    /// See <see cref="JsValue.ObjectIdentity"/>. A provider is required to make this canonical per
    /// object because handle equality is defined by it, which is exactly the promise a per-object
    /// store needs and the one this file used to reach through the engine's own type.
    /// </remarks>
    private static object IdentityOf(JsValue value) =>
        value.ObjectIdentity ?? throw new InvalidOperationException(
            "the blob store was keyed on a handle that is not an object");

    /// <summary>
    /// The live object URLs, newest last. An entry keeps its blob alive deliberately — that is what
    /// <c>createObjectURL</c> promises until <c>revokeObjectURL</c> is called, and the leak it implies
    /// is the page's to manage, exactly as in a browser.
    /// </summary>
    private readonly Dictionary<string, JsValue> _objectUrls = new(StringComparer.Ordinal);

    private int _nextObjectUrl;

    private sealed class BlobData(byte[] bytes, string type)
    {
        public byte[] Bytes { get; } = bytes;
        public string Type { get; } = type;

        /// <summary>Set for a <c>File</c>; <see langword="null"/> for a plain blob. Its presence is
        /// what makes the object a file.</summary>
        public string? Name { get; init; }

        public double LastModified { get; init; }
    }

    // -------- Registration --------

    /// <summary>
    /// Registers <c>Blob</c> and <c>File</c> and installs their members. Runs once per realm, with
    /// the other interface constructors.
    /// </summary>
    /// <remarks>
    /// <b>The realm is the bridge's own, and the overload that used to make one here is gone.</b>
    /// This module is built as <c>new BlobBinding()</c> rather than against a host contract, so it
    /// used to be handed the script context and adopt it — which minted a <em>second</em>
    /// <see cref="IJsRealm"/> over the one context, with a job queue of its own that nothing drained.
    /// The objects were the right ones (a handle carries the engine's own value), but a promise
    /// settled through the second realm reported to a queue no event loop pumped. The call site in
    /// <c>DomBridge/Registration/Polyfills.cs</c> passes the bridge's realm now, so there is one
    /// realm and one queue.
    /// </remarks>
    internal void RegisterInterfaces(IJsRealm realm)
    {
        // The host halves of the two constructors, captured into a closure and deleted from the
        // global so a page cannot mint one out of band.
        realm.SetProperty(realm.Global, "__broilerCreateBlob",
            realm.NewMethod("createBlob", (in call) => CreateBlob(in call, file: false), 2));
        realm.SetProperty(realm.Global, "__broilerCreateFile",
            realm.NewMethod("createFile", (in call) => CreateBlob(in call, file: true), 3));

        realm.EvaluateHostScript("""
            (function () {
                var createBlob = __broilerCreateBlob;
                var createFile = __broilerCreateFile;
                delete globalThis.__broilerCreateBlob;
                delete globalThis.__broilerCreateFile;

                // Both are real constructors — a page builds blobs and files itself, unlike most DOM
                // interfaces. Called without `new` they throw, as every interface object does.
                // Both parameters are optional, so Web IDL gives Blob a `length` of 0 and File a
                // length of 2 — which is why neither declares the optional ones and both read
                // `arguments` instead.
                function Blob() {
                    if (!new.target)
                        throw new TypeError("Failed to construct 'Blob': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
                    return createBlob(arguments[0], arguments[1]);
                }

                function File(parts, name) {
                    if (!new.target)
                        throw new TypeError("Failed to construct 'File': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
                    if (arguments.length < 2)
                        throw new TypeError("Failed to construct 'File': 2 arguments required, but only " + arguments.length + " present.");
                    return createFile(parts, name, arguments[2]);
                }

                // File IS a Blob (File API §3), so its prototype chain says so and `instanceof Blob`
                // answers through the chain rather than through a hook.
                Object.setPrototypeOf(File, Blob);
                Object.setPrototypeOf(File.prototype, Blob.prototype);

                Object.defineProperty(Blob.prototype, Symbol.toStringTag, {
                    value: 'Blob', writable: false, enumerable: false, configurable: true
                });
                Object.defineProperty(File.prototype, Symbol.toStringTag, {
                    value: 'File', writable: false, enumerable: false, configurable: true
                });

                globalThis.Blob = Blob;
                globalThis.File = File;
            })();
            """, "polyfill:blob");

        // Read back off the global rather than re-evaluated: the two names were just published
        // there, and a property read is the same answer the second Eval was asking for.
        var blobConstructor = realm.GetProperty(realm.Global, "Blob");
        var fileConstructor = realm.GetProperty(realm.Global, "File");
        if (!blobConstructor.IsObject || !fileConstructor.IsObject)
            return;

        var blobPrototype = realm.GetProperty(blobConstructor, "prototype");
        var filePrototype = realm.GetProperty(fileConstructor, "prototype");
        if (!blobPrototype.IsObject || !filePrototype.IsObject)
            return;

        _blobPrototype = blobPrototype;
        _filePrototype = filePrototype;

        Getter(realm, blobPrototype, "size", static data => JsValue.Number(data.Bytes.Length));
        Getter(realm, blobPrototype, "type", static data => JsValue.String(data.Type));
        Method(realm, blobPrototype, "slice", 0, Slice);
        Method(realm, blobPrototype, "text", 0, static (BlobData data, in JsCall call) =>
            Settled(call.Realm, JsValue.String(DecodeUtf8(data.Bytes))));
        Method(realm, blobPrototype, "arrayBuffer", 0, static (BlobData data, in JsCall call) =>
            Settled(call.Realm, call.Realm.NewArrayBuffer(data.Bytes)));

        // File's own three attributes. `lastModifiedDate` is legacy and a browser still carries it.
        Getter(realm, filePrototype, "name", static data => JsValue.String(data.Name ?? string.Empty));
        Getter(realm, filePrototype, "lastModified", static data => JsValue.Number(data.LastModified));
        Getter(realm, filePrototype, "webkitRelativePath", static _ => JsValue.String(string.Empty));
        Getter(realm, filePrototype, "lastModifiedDate", static data => JsValue.Number(data.LastModified));

        RegisterObjectUrls(realm);
    }

    /// <summary>
    /// <c>URL.createObjectURL</c> / <c>URL.revokeObjectURL</c>, installed on the <c>URL</c> the
    /// polyfill asset defines. They are statics on the interface object rather than members of a URL,
    /// so they go on after that constructor exists.
    /// </summary>
    private void RegisterObjectUrls(IJsRealm realm)
    {
        var url = realm.EvaluateHostScript("typeof URL === 'function' ? URL : null", "polyfill:blob-object-urls");
        if (!url.IsObject)
            return;

        realm.DefineValue(url, "createObjectURL", realm.NewMethod("createObjectURL", CreateObjectUrl, 1));
        realm.DefineValue(url, "revokeObjectURL", realm.NewMethod("revokeObjectURL", RevokeObjectUrl, 1));
    }


    // -------- Construction --------

    private JsValue CreateBlob(in JsCall call, bool file)
    {
        var realm = call.Realm;

        // File's own arguments are (parts, name, options); Blob's are (parts, options).
        var partsValue = call[0];
        var name = file ? (call.Length > 1 ? realm.ToJsString(call[1]) : string.Empty) : null;
        var options = file ? call[2] : call[1];
        if (!options.IsObject)
            options = JsValue.Missing;

        var bytes = CollectParts(realm, partsValue, file ? "File" : "Blob");
        var data = new BlobData(bytes, NormalizeType(TypeOption(realm, options)))
        {
            Name = name,
            LastModified = file ? ReadLastModified(realm, options) : 0,
        };

        return Mint(realm, data, file);
    }

    /// <summary>
    /// The <c>type</c> member of an options bag, or <see langword="null"/> when there is no bag or no
    /// such property.
    /// </summary>
    /// <remarks>
    /// Absent and <c>undefined</c> are deliberately different: the engine's indexer answered a CLR
    /// null for a property that is not there and this coerced only what it found, so
    /// <c>new Blob([], {type: undefined})</c> normalises the string <c>"undefined"</c> while
    /// <c>new Blob([], {})</c> normalises nothing. <see cref="JsValue.IsMissing"/> is that same
    /// absence, and preserving the difference is what keeps both answers what they were.
    /// </remarks>
    private static string? TypeOption(IJsRealm realm, JsValue options)
    {
        if (!options.IsObject)
            return null;

        var type = realm.GetProperty(options, "type");
        return type.IsMissing ? null : realm.ToJsString(type);
    }

    private JsValue Mint(IJsRealm realm, BlobData data, bool file)
    {
        var blob = realm.NewObject();
        _blobs.Add(IdentityOf(blob), data);
        var prototype = file ? _filePrototype : _blobPrototype;
        if (prototype.IsObject)
            realm.SetPrototype(blob, prototype);
        return blob;
    }

    /// <summary>
    /// The one seam other bindings mint blobs through — today <c>response.blob()</c>, which used to
    /// hand back a plain object of its own making.
    /// </summary>
    /// <param name="realm">The realm the blob object is minted in — the caller's, since it is the
    /// one whose <c>Blob</c> the page will compare against.</param>
    internal JsValue CreateBlobFromBytes(IJsRealm realm, byte[] bytes, string contentType) =>
        Mint(realm, new BlobData(bytes, NormalizeType(contentType)), file: false);

    /// <summary>
    /// The bytes behind a blob object, or <see langword="null"/> for anything that is not one. The
    /// seam the streams asset reads a blob through — its hook is captured into that closure and
    /// deleted from the global, so this does not become a way for a page to reach bytes out of band.
    /// </summary>
    /// <remarks>
    /// It takes a handle like everything else on this class; the unwrap to the object the weak table
    /// keys on happens in <see cref="TryDataFor"/>, in one place.
    /// </remarks>
    internal byte[]? BytesOf(JsValue candidate) =>
        TryDataFor(candidate, out var data) ? data.Bytes : null;

    private static double ReadLastModified(IJsRealm realm, JsValue options)
    {
        var value = options.IsObject ? realm.GetProperty(options, "lastModified") : JsValue.Missing;
        if (value.IsMissing || value.IsUndefined)
            // A File with no explicit timestamp reports "now"; the capture has no wall clock of its
            // own to prefer, so it uses the same one everything else here does.
            return Math.Floor((System.DateTime.UtcNow - System.DateTime.UnixEpoch).TotalMilliseconds);

        // The realm's ToNumber, not the handle's: this is the engine coercion the property read
        // performed, and a page may pass a string or an object with a valueOf.
        var number = realm.ToNumber(value);
        return double.IsNaN(number) ? 0 : Math.Truncate(number);
    }

    /// <summary>
    /// Flattens the parts sequence into one byte array. Web IDL converts the argument as a
    /// <c>sequence</c>, which deliberately does <em>not</em> accept a string — so
    /// <c>new Blob('abc')</c> is a <c>TypeError</c> and not a three-byte blob, which is the trap this
    /// argument sets for anyone reading the signature rather than measuring it.
    /// </summary>
    private byte[] CollectParts(IJsRealm realm, JsValue partsValue, string interfaceName)
    {
        if (partsValue.IsMissing || partsValue.IsUndefined)
            return [];

        if (!partsValue.IsArray)
            throw realm.Error(
                JsErrorKind.TypeError,
                $"Failed to construct '{interfaceName}': The provided value cannot be converted to a sequence.");

        var buffer = new List<byte>();
        var length = (int)NumberOrZero(realm, realm.GetProperty(partsValue, "length"));
        for (var i = 0; i < length; i++)
            buffer.AddRange(PartBytes(realm, realm.GetIndex(partsValue, (uint)i)));
        return [.. buffer];
    }

    /// <summary>
    /// One part's bytes. A <c>BufferSource</c> contributes its bytes and a <c>Blob</c> its own;
    /// anything else — including a number — is stringified and encoded as UTF-8, which is why
    /// <c>new Blob([123]).size</c> is 3.
    /// </summary>
    /// <remarks>
    /// The two <c>BufferSource</c> arms ask <see cref="IJsValues.TryGetArrayBufferBytes"/>, which is
    /// the only way to put the question: no JS-visible property distinguishes a buffer from an object
    /// carrying a <c>byteLength</c>, and both a prototype and a <c>Symbol.toStringTag</c> are things
    /// a page can write. The view's offset and length are still read through the JS-visible
    /// attributes a script would use, as they were, because a view needs no contract of its own.
    /// </remarks>
    private byte[] PartBytes(IJsRealm realm, JsValue part)
    {
        if (part.IsObject)
        {
            if (TryDataFor(part, out var nested))
                return nested.Bytes;

            if (realm.TryGetArrayBufferBytes(part, out var buffered))
                return buffered;

            // A typed array or DataView, read through the same JS-visible attributes a script would
            // use rather than through engine internals.
            var buffer = realm.GetProperty(part, "buffer");
            if (buffer.IsObject && realm.TryGetArrayBufferBytes(buffer, out var source))
            {
                var offset = (int)NumberOrZero(realm, realm.GetProperty(part, "byteOffset"));
                var byteLength = (int)NumberOrZero(realm, realm.GetProperty(part, "byteLength"));
                offset = Math.Clamp(offset, 0, source.Length);
                byteLength = Math.Clamp(byteLength, 0, source.Length - offset);
                return source.AsSpan(offset, byteLength).ToArray();
            }
        }

        return Encoding.UTF8.GetBytes(part.IsMissing ? string.Empty : realm.ToJsString(part));
    }

    /// <summary>
    /// A blob's <c>type</c> (File API §3.1): lower-cased, and dropped entirely if it carries a
    /// character outside the printable ASCII range rather than being escaped or kept.
    /// </summary>
    private static string NormalizeType(string? type)
    {
        if (string.IsNullOrEmpty(type))
            return string.Empty;

        foreach (var character in type)
        {
            if (character is < ' ' or > '~')
                return string.Empty;
        }

        return type.ToLowerInvariant();
    }

    // -------- Members --------

    private JsValue Slice(BlobData data, in JsCall call)
    {
        var realm = call.Realm;
        var length = data.Bytes.Length;
        var start = ClampRelative(realm, call[0], 0, length);
        var end = ClampRelative(realm, call[1], length, length);

        // A content type is only what the caller passes: the slice does NOT inherit the source's, so
        // `new Blob(['a'], {type: 'text/plain'}).slice(0, 1).type` is the empty string.
        var contentType = call.Length > 2 && !call[2].IsUndefined ? NormalizeType(realm.ToJsString(call[2])) : string.Empty;

        var count = Math.Max(0, end - start);
        return Mint(realm, new BlobData(data.Bytes.AsSpan(start, count).ToArray(), contentType), file: false);
    }

    /// <summary>A slice bound: absent means the default, negative counts back from the end, and
    /// everything is clamped into the blob.</summary>
    private static int ClampRelative(IJsRealm realm, JsValue value, int fallback, int length)
    {
        if (value.IsMissing || value.IsUndefined)
            return fallback;

        var number = realm.ToNumber(value);
        if (double.IsNaN(number))
            return 0;

        var index = Math.Truncate(number);
        if (index < 0)
            index = Math.Max(length + index, 0);
        return (int)Math.Clamp(index, 0, length);
    }

    // -------- Object URLs --------

    private JsValue CreateObjectUrl(in JsCall call)
    {
        if (call.Length == 0 || !TryDataFor(call[0], out _))
            throw call.Realm.Error(
                JsErrorKind.TypeError,
                "Failed to execute 'createObjectURL' on 'URL': Overload resolution failed.");

        // A browser's is `blob:<origin>/<uuid>`. The uuid is opaque by design — nothing may parse it
        // — so a counter is as good as a random one and keeps a capture reproducible.
        var url = $"blob:broiler/{++_nextObjectUrl:x8}-0000-4000-8000-000000000000";
        _objectUrls[url] = call[0];
        return JsValue.String(url);
    }

    private JsValue RevokeObjectUrl(in JsCall call)
    {
        if (call.Length > 0)
            _objectUrls.Remove(call.Realm.ToJsString(call[0]));
        return JsValue.Undefined;
    }

    /// <summary>The blob a live object URL names, for a fetch or a navigation that resolves one.
    /// <see langword="null"/> once revoked, or for a URL this document never minted.</summary>
    internal bool TryGetObjectUrlText(string url, out string text)
    {
        text = string.Empty;
        if (!_objectUrls.TryGetValue(url, out var blob) || !TryDataFor(blob, out var data))
            return false;

        text = DecodeUtf8(data.Bytes);
        return true;
    }

    // -------- Plumbing --------

    private delegate JsValue BlobOperation(BlobData data, in JsCall call);

    private void Method(IJsRealm realm, JsValue prototype, string name, int length, BlobOperation body) =>
        realm.DefineValue(prototype, name, realm.NewMethod(name, (in call) => body(DataFor(in call, name), in call), length));

    private void Getter(IJsRealm realm, JsValue prototype, string name, Func<BlobData, JsValue> read) =>
        realm.DefineAccessor(prototype, name, (in call) => read(DataFor(in call, name)), null);

    private BlobData DataFor(in JsCall call, string member)
    {
        if (TryDataFor(call.This, out var data))
            return data;

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to execute '{member}' on 'Blob': Illegal invocation");
    }

    /// <summary>
    /// A number read off an object, or zero when the property is not there at all.
    /// </summary>
    /// <remarks>
    /// The <c>?? 0</c> these reads carried applied to the engine indexer's CLR null — an absent
    /// property — and not to the coercion, so a property that is present and not numeric still
    /// answers NaN here exactly as it did. <see cref="JsValue.IsMissing"/> is that same absence.
    /// </remarks>
    private static double NumberOrZero(IJsRealm realm, JsValue value) =>
        value.IsMissing ? 0d : realm.ToNumber(value);

    /// <summary>The blob data behind a handle, for the receiver and argument tests.</summary>
    private bool TryDataFor(JsValue candidate, [MaybeNullWhen(false)] out BlobData data)
    {
        if (candidate.IsObject)
            return _blobs.TryGetValue(IdentityOf(candidate), out data);

        data = null;
        return false;
    }

    /// <summary>A promise already fulfilled with <paramref name="value"/>.</summary>
    /// <remarks>
    /// The realm hands back the settle functions rather than running an executor, so the promise is
    /// resolved here instead of inside a callback that only happened to run synchronously — the
    /// difference <see cref="IJsJobs.NewPromise"/> exists to remove.
    /// </remarks>
    private static JsValue Settled(IJsRealm realm, JsValue value)
    {
        var promise = realm.NewPromise(out var resolve, out _);
        resolve(value);
        return promise;
    }

    private static string DecodeUtf8(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);
}
