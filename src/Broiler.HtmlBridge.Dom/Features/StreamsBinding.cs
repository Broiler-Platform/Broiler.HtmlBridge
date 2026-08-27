using System.Text;
using Broiler.JavaScript.BuiltIns.Array.Typed;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
/// blob's bytes through it.
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
internal sealed class StreamsBinding
{
    /// <summary>
    /// The factory the asset exposes for "a stream over these bytes", captured here so the fetch
    /// body and <c>blob.stream()</c> both mint the same interface a page's own
    /// <c>new ReadableStream</c> does.
    /// </summary>
    private JSObject? _streamOverBytes;

    /// <summary>The same factory, for a stream that reports the first read or cancel — what a fetch
    /// body's <c>bodyUsed</c> is set from.</summary>
    private JSObject? _streamOverObservedBytes;

    /// <summary>Answers a stream's <c>locked</c> — a prototype accessor, so it is read in JavaScript
    /// where the receiver is unambiguous rather than through host indexing.</summary>
    private JSObject? _streamIsLocked;

    internal void Register(JSContext context, BlobBinding blobs)
    {
        context["__broilerBlobBytes"] = new DomFunction(
            (in a) => BytesOf(context, blobs, a.Length > 0 ? a[0] : JSUndefined.Value),
            "blobBytes",
            1);

        context.Eval(PolyfillAssets.Streams);

        _streamOverBytes = context["__broilerStreamOverBytes"] as JSObject;
        _streamOverObservedBytes = context["__broilerStreamOverObservedBytes"] as JSObject;
        _streamIsLocked = context["__broilerStreamIsLocked"] as JSObject;
        context.Eval("delete globalThis.__broilerStreamOverBytes;" +
                     "delete globalThis.__broilerStreamOverObservedBytes;" +
                     "delete globalThis.__broilerStreamIsLocked;");

        InstallBlobStream(context, blobs);
    }

    /// <summary>
    /// <c>blob.stream()</c> — a <c>ReadableStream</c> over the blob's bytes. Installed here rather
    /// than in <see cref="BlobBinding"/> because it is the one blob member that needs an interface
    /// registered after blobs are.
    /// </summary>
    private void InstallBlobStream(JSContext context, BlobBinding blobs)
    {
        if (context["Blob"] is not JSObject blobConstructor ||
            blobConstructor[(KeyString)"prototype"] is not JSObject blobPrototype)
            return;

        blobPrototype.FastAddValue(
            "stream",
            new DomFunction(
                (in a) => a.This is JSObject receiver && blobs.BytesOf(receiver) is { } bytes
                    ? StreamOverBytes(context, bytes)
                    : JSException.ThrowTypeError<JSValue>(
                        "Failed to execute 'stream' on 'Blob': Illegal invocation"),
                "stream",
                0),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>
    /// A <c>ReadableStream</c> delivering <paramref name="bytes"/> as one chunk and then closing.
    /// The seam a fetch body uses too, so a page reading <c>response.body</c> and a page reading
    /// <c>blob.stream()</c> get the same interface.
    /// </summary>
    internal JSValue StreamOverBytes(JSContext context, byte[] bytes)
    {
        if (_streamOverBytes is not { } factory)
            return JSNull.Value;

        return factory.InvokeFunction(new Arguments(JSUndefined.Value, ToArrayBuffer(bytes)));
    }

    /// <summary>A <c>ReadableStream</c> over the UTF-8 encoding of a text body.</summary>
    internal JSValue StreamOverText(JSContext context, string text) =>
        StreamOverBytes(context, Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// A <c>ReadableStream</c> over a text body that calls <paramref name="onDisturbed"/> the first
    /// time it is read or cancelled — the Body mixin's <c>bodyUsed</c>, which is what makes
    /// <c>text()</c>, <c>json()</c> and <c>clone()</c> refuse a body something has already consumed.
    /// </summary>
    internal JSValue StreamOverTextObserved(JSContext context, string text, Action onDisturbed)
    {
        if (_streamOverObservedBytes is not { } factory)
            return JSNull.Value;

        var reported = false;
        var report = new DomFunction((in _) =>
        {
            // Once: a stream pulls when a read arrives, and a body is disturbed the first time.
            if (!reported)
            {
                reported = true;
                onDisturbed();
            }

            return JSUndefined.Value;
        }, "disturbed", 0);

        return factory.InvokeFunction(new Arguments(
            JSUndefined.Value, ToArrayBuffer(Encoding.UTF8.GetBytes(text)), report));
    }

    /// <summary>Whether a reader holds <paramref name="stream"/>. <see langword="false"/> for
    /// anything that is not one of these streams.</summary>
    internal bool IsStreamLocked(JSValue stream) =>
        _streamIsLocked is { } locked &&
        locked.InvokeFunction(new Arguments(JSUndefined.Value, stream)).BooleanValue;

    private static JSValue BytesOf(JSContext context, BlobBinding blobs, JSValue candidate)
    {
        var bytes = blobs.BytesOf(candidate);
        if (bytes is null)
        {
            return JSException.ThrowTypeError<JSValue>(
                "The object provided is not a Blob.");
        }

        return ToArrayBuffer(bytes);
    }

    /// <summary>
    /// The bytes as an <c>ArrayBuffer</c>. The asset wraps it in a <c>Uint8Array</c> — the chunk type
    /// a browser's blob stream yields and what <c>FileReader</c>'s conversions read — because
    /// resolving the realm's <c>Uint8Array</c> from here is a lookup that can succeed at one call
    /// site and quietly hand back the bare buffer at another. A copy, so a page mutating a chunk
    /// cannot rewrite the blob it came from; blobs are immutable.
    /// </summary>
    private static JSValue ToArrayBuffer(byte[] bytes) => new JSArrayBuffer((byte[])bytes.Clone());
}
