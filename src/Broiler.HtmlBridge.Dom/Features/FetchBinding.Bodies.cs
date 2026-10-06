using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Request and response bodies as bytes: Fetch's "extract a body" for what a page sends through
/// <c>fetch()</c>, <c>new Request</c>, <c>new Response</c>, <c>XMLHttpRequest</c> and
/// <c>navigator.sendBeacon</c>, and the bytes a response's readers hand back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bodies used to be strings, and that broke binary ones.</b> Every body was the realm's string
/// conversion of what the page passed, sent as UTF-8 text with .NET's default
/// <c>text/plain; charset=utf-8</c>. A <c>Uint8Array</c> went out as its elements joined with commas
/// (<c>0,1,2,255</c>), an <c>ArrayBuffer</c>, a <c>Blob</c> or a <c>FormData</c> as
/// <c>[object ArrayBuffer]</c> and the like. reCAPTCHA's checkbox posts its verification as a binary
/// protobuf, a <c>Uint8Array</c> under <c>application/x-protobuffer</c>, so the request Google received
/// could not be parsed and the check never came. Responses were decoded as text by their charset and
/// re-encoded as UTF-8 for <c>arrayBuffer()</c> and <c>blob()</c>, so a binary response came back
/// altered and longer.
/// </para>
/// <para>
/// <b>What a body is, measured in Chromium.</b> An <c>ArrayBuffer</c> or a view of one (a typed
/// array, a <c>DataView</c>, a <c>subarray</c>) is the view's bytes, with no <c>Content-Type</c>. A
/// <c>Blob</c> is its bytes, typed by its type when it has one. A <c>URLSearchParams</c> is its
/// serialization, <c>application/x-www-form-urlencoded;charset=UTF-8</c>. A <c>FormData</c> is
/// <c>multipart/form-data</c> with a <c>----WebKitFormBoundary</c> boundary. Anything else is its
/// string conversion as UTF-8, <c>text/plain;charset=UTF-8</c>. The type is the default only: a
/// <c>Content-Type</c> the page set wins. Reading a body: <c>text()</c> and <c>json()</c> decode UTF-8
/// whatever the charset (a byte-order mark dropped, a malformed sequence U+FFFD), and
/// <c>arrayBuffer()</c> and <c>blob()</c> are the bytes as received.
/// </para>
/// <para>
/// A <c>ReadableStream</c> body is not supported: it is converted to a string as before. Chromium
/// sends one only with <c>duplex: 'half'</c>.
/// </para>
/// </remarks>
internal sealed partial class FetchBinding
{
    private const string TextPlain = "text/plain;charset=UTF-8";
    private const string FormUrlEncoded = "application/x-www-form-urlencoded;charset=UTF-8";

    /// <summary>A body and the <c>Content-Type</c> it implies: Fetch's "body with type".</summary>
    /// <param name="Bytes">The body.</param>
    /// <param name="ContentType">The type it implies, or <see langword="null"/> for none.</param>
    private readonly record struct BodyWithType(byte[] Bytes, string? ContentType);

    /// <summary>The realm's <c>ArrayBuffer.isView</c>, read when the surface is installed.</summary>
    private JsValue _isArrayBufferView;

    /// <summary>
    /// <c>URLSearchParams.prototype</c> as the polyfill defined it, read once it is installed
    /// (<see cref="AdoptUrlSearchParams"/>), so a page that replaces the global does not change what
    /// a body is.
    /// </summary>
    private JsValue _urlSearchParamsPrototype;

    /// <summary>The entries behind each <c>FormData</c> this binding made, for its multipart body.</summary>
    private readonly ConditionalWeakTable<object, List<KeyValuePair<string, JsValue>>> _formDataEntries = new();

    /// <summary>The body of each <c>Request</c> this binding made that has one.</summary>
    private readonly ConditionalWeakTable<object, StoredBody> _requestBodies = new();

    /// <summary>The body of each <c>Response</c> this binding made.</summary>
    private readonly ConditionalWeakTable<object, StoredBody> _responseBodies = new();

    /// <summary>A request's or response's body, held for the life of its object.</summary>
    private sealed class StoredBody(byte[] bytes, string? contentType)
    {
        public byte[] Bytes { get; } = bytes;

        /// <summary>The <c>Content-Type</c> the object had when it was made: what XHR decodes a response's text by.</summary>
        public string? ContentType { get; } = contentType;
    }

    /// <summary>
    /// Takes <c>URLSearchParams.prototype</c> from the global, which the content-rendering polyfill has
    /// just defined. Called once, before a page's first script.
    /// </summary>
    internal void AdoptUrlSearchParams(IJsRealm realm)
    {
        var constructor = realm.GetProperty(realm.Global, "URLSearchParams");
        if (constructor.IsFunction)
            _urlSearchParamsPrototype = realm.GetProperty(constructor, "prototype");
    }

    private static object IdentityOf(JsValue value) =>
        value.ObjectIdentity ?? throw new InvalidOperationException("a body was keyed on a handle that is not an object");

    /// <summary>The body a <c>Request</c> this binding made holds, or <see langword="null"/> for none.</summary>
    private StoredBody? RequestBodyOf(JsValue request) =>
        request.IsObject && _requestBodies.TryGetValue(IdentityOf(request), out var stored) ? stored : null;

    /// <summary>The body a <c>Response</c> this binding made holds, or <see langword="null"/>.</summary>
    private StoredBody? ResponseBodyOf(JsValue response) =>
        response.IsObject && _responseBodies.TryGetValue(IdentityOf(response), out var stored) ? stored : null;

    /// <summary>
    /// Fetch's "extract a body" for <paramref name="value"/>: its bytes and the <c>Content-Type</c> they
    /// imply (see the type remarks).
    /// </summary>
    private BodyWithType ExtractBody(IJsRealm realm, JsValue value)
    {
        if (value.IsObject)
        {
            if (_host.BlobContentOf(value) is { } blob)
                return new BodyWithType(blob.Bytes, blob.Type.Length > 0 ? blob.Type : null);

            if (BufferSources.TryGetBytes(realm, value, _isArrayBufferView, out var buffered))
                return new BodyWithType(buffered, null);

            if (_formDataEntries.TryGetValue(IdentityOf(value), out var entries))
                return EncodeMultipart(entries);

            if (IsUrlSearchParams(realm, value))
                return new BodyWithType(Encoding.UTF8.GetBytes(realm.ToJsString(value)), FormUrlEncoded);
        }

        // ToJsString, not the handle's rendering: a page's own payload object decides the bytes through
        // its toString, and that coercion is observable.
        return new BodyWithType(Encoding.UTF8.GetBytes(realm.ToJsString(value)), TextPlain);
    }

    private bool IsUrlSearchParams(IJsRealm realm, JsValue candidate)
    {
        if (!_urlSearchParamsPrototype.IsObject)
            return false;

        // The prototype chain, as instanceof walks it, against the polyfill's own prototype.
        var prototype = realm.GetPrototype(candidate);
        for (var depth = 0; prototype.IsObject && depth < 64; depth++)
        {
            if (prototype == _urlSearchParamsPrototype)
                return true;
            prototype = realm.GetPrototype(prototype);
        }

        return false;
    }

    /// <summary>
    /// A <c>FormData</c>'s entries as <c>multipart/form-data</c>, framed as Chromium frames them.
    /// </summary>
    /// <remarks>
    /// Measured: the boundary is <c>----WebKitFormBoundary</c> and 16 letters and digits; a name has
    /// its line breaks made CRLF and then <c>"</c>, CR and LF percent-encoded; a file name is only
    /// percent-encoded; a string value has its line breaks made CRLF; a file's part names it and its
    /// type, <c>application/octet-stream</c> when it has none.
    /// </remarks>
    private BodyWithType EncodeMultipart(List<KeyValuePair<string, JsValue>> entries)
    {
        var boundary = "----WebKitFormBoundary" + RandomNumberGenerator.GetString(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 16);

        using var body = new MemoryStream();
        void Write(string text) => body.Write(Encoding.UTF8.GetBytes(text));

        foreach (var (name, value) in entries)
        {
            Write("--" + boundary + "\r\n");
            Write("Content-Disposition: form-data; name=\"" + EscapeMultipartName(NormalizeLineBreaks(name)) + "\"");
            if (!value.IsString && _host.BlobContentOf(value) is { } file)
            {
                Write("; filename=\"" + EscapeMultipartName(_host.FileNameOf(value) ?? "blob") + "\"\r\n");
                Write("Content-Type: " + (file.Type.Length > 0 ? file.Type : "application/octet-stream") + "\r\n\r\n");
                body.Write(file.Bytes);
            }
            else
            {
                Write("\r\n\r\n");
                Write(NormalizeLineBreaks(value.IsString ? value.AsString! : string.Empty));
            }

            Write("\r\n");
        }

        Write("--" + boundary + "--\r\n");
        return new BodyWithType(body.ToArray(), "multipart/form-data; boundary=" + boundary);

        static string NormalizeLineBreaks(string text) =>
            text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);

        static string EscapeMultipartName(string text) =>
            text.Replace("\"", "%22", StringComparison.Ordinal)
                .Replace("\r", "%0D", StringComparison.Ordinal)
                .Replace("\n", "%0A", StringComparison.Ordinal);
    }

    /// <summary>
    /// Fetch's "UTF-8 decode": a leading byte-order mark dropped, every malformed sequence U+FFFD. What
    /// <c>text()</c> and <c>json()</c> read, whatever charset the response named.
    /// </summary>
    private static string DecodeUtf8(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            span = span[3..];
        return Encoding.UTF8.GetString(span);
    }

    /// <summary>
    /// The MIME type a <c>Content-Type</c> value names, lower-cased and without its parameters, or the
    /// empty string: the <c>type</c> of the <c>Blob</c> that <c>blob()</c> answers (measured: a
    /// <c>text/plain; charset=iso-8859-1</c> response gives <c>text/plain</c>).
    /// </summary>
    private static string EssenceOf(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return string.Empty;

        var semicolon = contentType.IndexOf(';');
        return (semicolon >= 0 ? contentType[..semicolon] : contentType).Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The text of a response for <c>XMLHttpRequest</c>'s <c>responseText</c>: decoded by the charset of
    /// the override MIME type when the page set one, else of the response's <c>Content-Type</c>, else
    /// as UTF-8 (measured: an <c>iso-8859-1</c> response reads as such through XHR, where
    /// <c>response.text()</c> reads it as UTF-8).
    /// </summary>
    private JsValue DecodeXhrText(in JsCall call)
    {
        if (ResponseBodyOf(call[0]) is not { } stored)
            return JsValue.String(string.Empty);

        var mimeOverride = call.Length > 1 && !call[1].IsNullish ? call.Realm.ToJsString(call[1]) : null;
        var encoding = EncodingOf(CharsetOf(mimeOverride) ?? CharsetOf(stored.ContentType));
        if (encoding is null || encoding.CodePage == Encoding.UTF8.CodePage)
            return JsValue.String(DecodeUtf8(stored.Bytes));

        return JsValue.String(encoding.GetString(stored.Bytes));

        static string? CharsetOf(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType))
                return null;

            foreach (var parameter in contentType.Split(';').Skip(1))
            {
                var equals = parameter.IndexOf('=');
                if (equals > 0 && parameter[..equals].Trim().Equals("charset", StringComparison.OrdinalIgnoreCase))
                    return parameter[(equals + 1)..].Trim().Trim('"');
            }

            return null;
        }

        static Encoding? EncodingOf(string? label)
        {
            if (string.IsNullOrEmpty(label))
                return null;

            // The Encoding Standard reads every Latin-1 and ASCII label as windows-1252, which differs
            // from ISO-8859-1 in 0x80-0x9F; .NET has it only from the code-page provider, used here
            // without registering it for the whole process.
            var normalized = label.Trim().ToLowerInvariant();
            if (normalized is "iso-8859-1" or "iso8859-1" or "iso_8859-1" or "latin1" or "l1" or "us-ascii" or "ascii"
                or "windows-1252" or "cp1252" or "x-cp1252" or "cp819" or "ibm819")
                return CodePagesEncodingProvider.Instance.GetEncoding(1252);

            try
            {
                return CodePagesEncodingProvider.Instance.GetEncoding(normalized) ?? Encoding.GetEncoding(normalized);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }
}
