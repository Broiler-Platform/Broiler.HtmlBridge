using System.Text;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Registers the streams and File-reader asset and the two things that cannot live inside it: the
/// host hook that reads a blob's bytes, and <c>Blob.prototype.stream()</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReadableStream</c> did not exist, and what stood in for it was a shape-only object that
/// <c>response.body</c> handed back: it carried a <c>getReader</c> whose reader had <c>read</c>,
/// <c>cancel</c> and <c>releaseLock</c> and nothing else — no <c>closed</c>, no <c>tee</c>, no
/// <c>cancel</c> on the stream itself, and no constructor for a page to build one
/// of its own. So <c>new ReadableStream(...)</c> was a <c>ReferenceError</c>, which aborts the script
/// rather than the statement.
/// <c>Blob.prototype.stream()</c> was left out for exactly this reason and is now in.
/// </para>
/// <para>
/// <b>The stream is JavaScript.</b> The specification is written as a state machine over promises —
/// a queue, a list of pending read requests, and a pull signal that must not re-enter — and
/// expressing that in host functions would mean re-deriving the promise plumbing the engine already
/// has. The one thing the host provides is a blob's bytes, because that is where blobs live; its
/// hook is captured into the asset's closure and deleted from the global, so a page cannot reach a
/// blob's bytes through it. The asset is script this repository authored, so it runs through
/// <see cref="IJsSource.EvaluateHostScript"/> and is not subject to the page's content policy.
/// </para>
/// <para>
/// <b>Not implemented, and detectably so:</b> <c>pipeTo</c> and <c>pipeThrough</c>, which need a
/// <c>WritableStream</c>, and BYOB readers, which need a byte-stream controller, so
/// <c>getReader({mode: 'byob'})</c> throws rather than handing back a default reader that would
/// ignore the caller's buffer.
/// </para>
/// <para>
/// Async iteration — <c>values()</c> and <c>@@asyncIterator</c> — <b>is</b> implemented. It was the
/// one piece held back when the rest landed, because <c>for await</c> deadlocked the agent on an
/// iterator whose <c>next()</c> returned a promise that was not already settled, which is exactly
/// what an iterator over a stream returns. The engine fix is upstream and the pinned
/// <c>Broiler.JS</c> pointer carries it.
/// </para>
/// </remarks>
/// <param name="realm">
/// How the module reaches the realm it installs into. A function rather than the realm itself
/// because the bridge builds this module in its constructor and adopts its realm only when a
/// document is attached — the same reason every other feature module takes a host contract it
/// queries later rather than a value it captures now.
/// </param>
internal sealed class StreamsBinding(Func<IJsRealm> realm)
{
    private readonly Func<IJsRealm> _realm = realm;

    /// <summary>
    /// The factory the asset exposes for "a stream over these bytes", captured here so the fetch
    /// body and <c>blob.stream()</c> both mint the same interface a page's own
    /// <c>new ReadableStream</c> does.
    /// </summary>
    private JsValue _streamOverBytes;

    /// <summary>The same factory, for a stream that reports the first read or cancel — what a fetch
    /// body's <c>bodyUsed</c> is set from.</summary>
    private JsValue _streamOverObservedBytes;

    /// <summary>Answers a stream's <c>locked</c> — a prototype accessor, so it is read in JavaScript
    /// where the receiver is unambiguous rather than through host indexing.</summary>
    private JsValue _streamIsLocked;

    /// <summary>
    /// Registers the streams asset and <c>Blob.prototype.stream()</c> into the realm this module was
    /// constructed with.
    /// </summary>
    /// <param name="blobs">The blob store the byte hook reads from.</param>
    internal void Register(BlobBinding blobs)
    {
        var realm = _realm();

        realm.SetProperty(
            realm.Global,
            "__broilerBlobBytes",
            realm.NewMethod("blobBytes", (in call) => BytesOf(blobs, in call), 1));

        realm.EvaluateHostScript(PolyfillAssets.Streams, "polyfill:streams");

        _streamOverBytes = realm.GetProperty(realm.Global, "__broilerStreamOverBytes");
        _streamOverObservedBytes = realm.GetProperty(realm.Global, "__broilerStreamOverObservedBytes");
        _streamIsLocked = realm.GetProperty(realm.Global, "__broilerStreamIsLocked");
        realm.EvaluateHostScript(
            "delete globalThis.__broilerStreamOverBytes;" +
            "delete globalThis.__broilerStreamOverObservedBytes;" +
            "delete globalThis.__broilerStreamIsLocked;",
            "polyfill:streams-cleanup");

        InstallBlobStream(realm, blobs);
    }

    /// <summary>
    /// <c>blob.stream()</c> — a <c>ReadableStream</c> over the blob's bytes. Installed here rather
    /// than in <see cref="BlobBinding"/> because it is the one blob member that needs an interface
    /// registered after blobs are.
    /// </summary>
    private void InstallBlobStream(IJsRealm realm, BlobBinding blobs)
    {
        var blobConstructor = realm.GetProperty(realm.Global, "Blob");
        if (!blobConstructor.IsObject)
            return;

        var blobPrototype = realm.GetProperty(blobConstructor, "prototype");
        if (!blobPrototype.IsObject)
            return;

        realm.DefineValue(
            blobPrototype,
            "stream",
            realm.NewMethod(
                "stream",
                (in call) => BytesOfBlob(blobs, call.This) is { } bytes
                    ? StreamOverBytes(bytes)
                    : throw call.Realm.Error(
                        JsErrorKind.TypeError,
                        "Failed to execute 'stream' on 'Blob': Illegal invocation"),
                0));
    }

    /// <summary>
    /// A blob's bytes, or <see langword="null"/> when the value is not one.
    /// </summary>
    /// <remarks>
    /// The non-object arm stays here as well as in <see cref="BlobBinding"/>: only an object can be a
    /// blob, because the store keys on object identity, so both sides answer the same and neither
    /// depends on the other having asked.
    /// </remarks>
    private static byte[]? BytesOfBlob(BlobBinding blobs, JsValue candidate) =>
        candidate.IsObject ? blobs.BytesOf(candidate) : null;

    /// <summary>
    /// A <c>ReadableStream</c> delivering <paramref name="bytes"/> as one chunk and then closing.
    /// The seam a fetch body uses too, so a page reading <c>response.body</c> and a page reading
    /// <c>blob.stream()</c> get the same interface.
    /// </summary>
    private JsValue StreamOverBytes(byte[] bytes)
    {
        if (!_streamOverBytes.IsObject)
            return JsValue.Null;

        return _realm().Invoke(_streamOverBytes, JsValue.Undefined, [ToArrayBuffer(bytes)]);
    }

    /// <summary>A <c>ReadableStream</c> over the UTF-8 encoding of a text body.</summary>
    internal JsValue StreamOverText(string text) =>
        StreamOverBytes(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// A <c>ReadableStream</c> over a text body that calls <paramref name="onDisturbed"/> the first
    /// time it is read or cancelled — the Body mixin's <c>bodyUsed</c>, which is what makes
    /// <c>text()</c>, <c>json()</c> and <c>clone()</c> refuse a body something has already consumed.
    /// </summary>
    internal JsValue StreamOverTextObserved(string text, Action onDisturbed)
    {
        if (!_streamOverObservedBytes.IsObject)
            return JsValue.Null;

        var realm = _realm();
        var reported = false;
        var report = realm.NewMethod("disturbed", (in _) =>
        {
            // Once: a stream pulls when a read arrives, and a body is disturbed the first time.
            if (!reported)
            {
                reported = true;
                onDisturbed();
            }

            return JsValue.Undefined;
        }, 0);

        return realm.Invoke(
            _streamOverObservedBytes,
            JsValue.Undefined,
            [ToArrayBuffer(Encoding.UTF8.GetBytes(text)), report]);
    }

    /// <summary>Whether a reader holds <paramref name="stream"/>. <see langword="false"/> for
    /// anything that is not one of these streams.</summary>
    /// <remarks>
    /// Only an object can be one of these streams, so a non-object is refused here and the JavaScript
    /// predicate is asked about the rest — the same two arms the engine-typed form had, with the
    /// object test now on the handle's own kind.
    /// </remarks>
    internal bool IsStreamLocked(JsValue stream)
    {
        if (!_streamIsLocked.IsObject || !stream.IsObject)
            return false;

        return _realm()
            .Invoke(_streamIsLocked, JsValue.Undefined, [stream])
            .AsBoolean;
    }

    /// <summary>
    /// <c>__broilerBlobBytes(blob)</c> — the asset's one host hook.
    /// </summary>
    private static JsValue BytesOf(BlobBinding blobs, in JsCall call)
    {
        var bytes = BytesOfBlob(blobs, call[0]);
        if (bytes is null)
            throw call.Realm.Error(JsErrorKind.TypeError, "The object provided is not a Blob.");

        return ToArrayBuffer(bytes);
    }

    /// <summary>
    /// The bytes as an <c>ArrayBuffer</c>. The asset wraps it in a <c>Uint8Array</c> — the chunk type
    /// a browser's blob stream yields and what <c>FileReader</c>'s conversions read — because
    /// resolving the realm's <c>Uint8Array</c> from here is a lookup that can succeed at one call
    /// site and quietly hand back the bare buffer at another. A copy, so a page mutating a chunk
    /// cannot rewrite the blob it came from; blobs are immutable.
    /// </summary>
    /// <remarks>
    /// <b>This is the one line JSEAL cannot express.</b> <see cref="IJsValues"/> mints objects,
    /// arrays and functions; it has no ArrayBuffer or typed-array member, and there is no capability
    /// flag for one. Until the contract grows one, the buffer is built with the engine's own type and
    /// handed across as a handle.
    /// </remarks>
    private static JsValue ToArrayBuffer(byte[] bytes) =>
        Runtime.JsInterop.FromEngineObject(
            new Broiler.JavaScript.BuiltIns.Array.Typed.JSArrayBuffer((byte[])bytes.Clone()));
}
