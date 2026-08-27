using System.Runtime.CompilerServices;
using System.Text;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Array.Typed;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.Promise;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
/// </remarks>
internal sealed class BlobBinding
{
    private JSObject? _blobPrototype;
    private JSObject? _filePrototype;

    /// <summary>The bytes and metadata behind each blob object. Weak, so a blob a page has dropped is
    /// not kept alive by this table.</summary>
    private readonly ConditionalWeakTable<JSObject, BlobData> _blobs = new();

    /// <summary>
    /// The live object URLs, newest last. An entry keeps its blob alive deliberately — that is what
    /// <c>createObjectURL</c> promises until <c>revokeObjectURL</c> is called, and the leak it implies
    /// is the page's to manage, exactly as in a browser.
    /// </summary>
    private readonly Dictionary<string, JSObject> _objectUrls = new(StringComparer.Ordinal);

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
    /// Registers <c>Blob</c> and <c>File</c> and installs their members. Runs once per context, with
    /// the other interface constructors.
    /// </summary>
    internal void RegisterInterfaces(JSContext context)
    {
        // The host halves of the two constructors, captured into a closure and deleted from the
        // global so a page cannot mint one out of band.
        context["__broilerCreateBlob"] = new DomFunction((in a) => CreateBlob(in a, file: false), "createBlob", 2);
        context["__broilerCreateFile"] = new DomFunction((in a) => CreateBlob(in a, file: true), "createFile", 3);

        context.Eval("""
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
            """);

        if (context.Eval("Blob") is not JSObject blobConstructor ||
            blobConstructor[(KeyString)"prototype"] is not JSObject blobPrototype ||
            context.Eval("File") is not JSObject fileConstructor ||
            fileConstructor[(KeyString)"prototype"] is not JSObject filePrototype)
            return;

        _blobPrototype = blobPrototype;
        _filePrototype = filePrototype;

        Getter(blobPrototype, "size", static data => new JSNumber(data.Bytes.Length));
        Getter(blobPrototype, "type", static data => new JSString(data.Type));
        Method(blobPrototype, "slice", 0, Slice);
        Method(blobPrototype, "text", 0, static (BlobData data, in Arguments _) =>
            new JSPromise((resolve, _) => resolve(new JSString(DecodeUtf8(data.Bytes)))));
        Method(blobPrototype, "arrayBuffer", 0, static (BlobData data, in Arguments _) =>
            new JSPromise((resolve, _) => resolve(new JSArrayBuffer((byte[])data.Bytes.Clone()))));

        // File's own three attributes. `lastModifiedDate` is legacy and a browser still carries it.
        Getter(filePrototype, "name", static data => new JSString(data.Name ?? string.Empty));
        Getter(filePrototype, "lastModified", static data => new JSNumber(data.LastModified));
        Getter(filePrototype, "webkitRelativePath", static _ => new JSString(string.Empty));
        Getter(filePrototype, "lastModifiedDate", static data => new JSNumber(data.LastModified));

        RegisterObjectUrls(context);
    }

    /// <summary>
    /// <c>URL.createObjectURL</c> / <c>URL.revokeObjectURL</c>, installed on the <c>URL</c> the
    /// polyfill asset defines. They are statics on the interface object rather than members of a URL,
    /// so they go on after that constructor exists.
    /// </summary>
    private void RegisterObjectUrls(JSContext context)
    {
        if (context.Eval("typeof URL === 'function' ? URL : null") is not JSObject url)
            return;

        url.FastAddValue(
            "createObjectURL",
            new DomFunction((in a) => CreateObjectUrl(in a), "createObjectURL", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        url.FastAddValue(
            "revokeObjectURL",
            new DomFunction((in a) => RevokeObjectUrl(in a), "revokeObjectURL", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    // -------- Construction --------

    private JSValue CreateBlob(in Arguments a, bool file)
    {
        // File's own arguments are (parts, name, options); Blob's are (parts, options).
        var partsValue = a.Length > 0 ? a[0] : null;
        var name = file ? (a.Length > 1 ? a[1].ToString() : string.Empty) : null;
        var options = (file ? (a.Length > 2 ? a[2] : null) : (a.Length > 1 ? a[1] : null)) as JSObject;

        var bytes = CollectParts(partsValue, file ? "File" : "Blob");
        var data = new BlobData(bytes, NormalizeType(options?[(KeyString)"type"]?.ToString()))
        {
            Name = name,
            LastModified = file ? ReadLastModified(options) : 0,
        };

        return Mint(data, file);
    }

    private JSObject Mint(BlobData data, bool file)
    {
        var blob = new JSObject();
        _blobs.Add(blob, data);
        var prototype = file ? _filePrototype : _blobPrototype;
        if (prototype is { } p)
            blob.BasePrototypeObject = p;
        return blob;
    }

    /// <summary>
    /// The one seam other bindings mint blobs through — today <c>response.blob()</c>, which used to
    /// hand back a plain object of its own making.
    /// </summary>
    internal JSValue CreateBlobFromBytes(byte[] bytes, string contentType) =>
        Mint(new BlobData(bytes, NormalizeType(contentType)), file: false);

    /// <summary>
    /// The bytes behind a blob object, or <see langword="null"/> for anything that is not one. The
    /// seam the streams asset reads a blob through — its hook is captured into that closure and
    /// deleted from the global, so this does not become a way for a page to reach bytes out of band.
    /// </summary>
    internal byte[]? BytesOf(JSValue candidate) =>
        candidate is JSObject blob && _blobs.TryGetValue(blob, out var data) ? data.Bytes : null;

    private static double ReadLastModified(JSObject? options)
    {
        var value = options?[(KeyString)"lastModified"];
        if (value is null || value.IsUndefined)
            // A File with no explicit timestamp reports "now"; the capture has no wall clock of its
            // own to prefer, so it uses the same one everything else here does.
            return Math.Floor((System.DateTime.UtcNow - System.DateTime.UnixEpoch).TotalMilliseconds);

        var number = value.DoubleValue;
        return double.IsNaN(number) ? 0 : Math.Truncate(number);
    }

    /// <summary>
    /// Flattens the parts sequence into one byte array. Web IDL converts the argument as a
    /// <c>sequence</c>, which deliberately does <em>not</em> accept a string — so
    /// <c>new Blob('abc')</c> is a <c>TypeError</c> and not a three-byte blob, which is the trap this
    /// argument sets for anyone reading the signature rather than measuring it.
    /// </summary>
    private byte[] CollectParts(JSValue? partsValue, string interfaceName)
    {
        if (partsValue is null || partsValue.IsUndefined)
            return [];

        if (partsValue is not JSArray parts)
            return JSException.ThrowTypeError<byte[]>(
                $"Failed to construct '{interfaceName}': The provided value cannot be converted to a sequence.");

        var buffer = new List<byte>();
        var length = (int)(parts[(KeyString)"length"]?.DoubleValue ?? 0);
        for (var i = 0; i < length; i++)
            buffer.AddRange(PartBytes(parts[(uint)i]));
        return [.. buffer];
    }

    /// <summary>
    /// One part's bytes. A <c>BufferSource</c> contributes its bytes and a <c>Blob</c> its own;
    /// anything else — including a number — is stringified and encoded as UTF-8, which is why
    /// <c>new Blob([123]).size</c> is 3.
    /// </summary>
    private byte[] PartBytes(JSValue? part)
    {
        if (part is JSObject candidate)
        {
            if (_blobs.TryGetValue(candidate, out var nested))
                return nested.Bytes;

            if (candidate is JSArrayBuffer arrayBuffer)
                return arrayBuffer.Buffer;

            // A typed array or DataView, read through the same JS-visible attributes a script would
            // use rather than through engine internals.
            if (candidate[(KeyString)"buffer"] is JSArrayBuffer viewBuffer)
            {
                var offset = (int)(candidate[(KeyString)"byteOffset"]?.DoubleValue ?? 0);
                var byteLength = (int)(candidate[(KeyString)"byteLength"]?.DoubleValue ?? 0);
                var source = viewBuffer.Buffer;
                offset = Math.Clamp(offset, 0, source.Length);
                byteLength = Math.Clamp(byteLength, 0, source.Length - offset);
                return source.AsSpan(offset, byteLength).ToArray();
            }
        }

        return Encoding.UTF8.GetBytes(part?.ToString() ?? string.Empty);
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

    private JSValue Slice(BlobData data, in Arguments a)
    {
        var length = data.Bytes.Length;
        var start = ClampRelative(a.Length > 0 ? a[0] : null, 0, length);
        var end = ClampRelative(a.Length > 1 ? a[1] : null, length, length);

        // A content type is only what the caller passes: the slice does NOT inherit the source's, so
        // `new Blob(['a'], {type: 'text/plain'}).slice(0, 1).type` is the empty string.
        var contentType = a.Length > 2 && !a[2].IsUndefined ? NormalizeType(a[2].ToString()) : string.Empty;

        var count = Math.Max(0, end - start);
        return Mint(new BlobData(data.Bytes.AsSpan(start, count).ToArray(), contentType), file: false);
    }

    /// <summary>A slice bound: absent means the default, negative counts back from the end, and
    /// everything is clamped into the blob.</summary>
    private static int ClampRelative(JSValue? value, int fallback, int length)
    {
        if (value is null || value.IsUndefined)
            return fallback;

        var number = value.DoubleValue;
        if (double.IsNaN(number))
            return 0;

        var index = Math.Truncate(number);
        if (index < 0)
            index = Math.Max(length + index, 0);
        return (int)Math.Clamp(index, 0, length);
    }

    // -------- Object URLs --------

    private JSValue CreateObjectUrl(in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject candidate || !_blobs.TryGetValue(candidate, out _))
            return JSException.ThrowTypeError<JSValue>(
                "Failed to execute 'createObjectURL' on 'URL': Overload resolution failed.");

        // A browser's is `blob:<origin>/<uuid>`. The uuid is opaque by design — nothing may parse it
        // — so a counter is as good as a random one and keeps a capture reproducible.
        var url = $"blob:broiler/{++_nextObjectUrl:x8}-0000-4000-8000-000000000000";
        _objectUrls[url] = candidate;
        return new JSString(url);
    }

    private JSValue RevokeObjectUrl(in Arguments a)
    {
        if (a.Length > 0)
            _objectUrls.Remove(a[0].ToString());
        return JSUndefined.Value;
    }

    /// <summary>The blob a live object URL names, for a fetch or a navigation that resolves one.
    /// <see langword="null"/> once revoked, or for a URL this document never minted.</summary>
    internal bool TryGetObjectUrlText(string url, out string text)
    {
        text = string.Empty;
        if (!_objectUrls.TryGetValue(url, out var blob) || !_blobs.TryGetValue(blob, out var data))
            return false;

        text = DecodeUtf8(data.Bytes);
        return true;
    }

    // -------- Plumbing --------

    private delegate JSValue BlobOperation(BlobData data, in Arguments a);

    private void Method(JSObject prototype, string name, int length, BlobOperation body) =>
        prototype.FastAddValue(
            name,
            new DomFunction((in a) => body(DataFor(in a, name), in a), name, length),
            JSPropertyAttributes.EnumerableConfigurableValue);

    private void Getter(JSObject prototype, string name, Func<BlobData, JSValue> read) =>
        prototype.FastAddProperty(
            name,
            new DomFunction((in a) => read(DataFor(in a, name)), $"get {name}"),
            null,
            JSPropertyAttributes.EnumerableConfigurableProperty);

    private BlobData DataFor(in Arguments a, string member)
    {
        if (a.This is JSObject receiver && _blobs.TryGetValue(receiver, out var data))
            return data;

        return JSException.ThrowTypeError<BlobData>(
            $"Failed to execute '{member}' on 'Blob': Illegal invocation");
    }

    private static string DecodeUtf8(byte[] bytes) => new UTF8Encoding(false).GetString(bytes);
}
