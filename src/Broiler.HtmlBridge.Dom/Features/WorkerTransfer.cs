using System;
using System.Collections.Generic;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Array.Typed;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Validates a <c>postMessage</c> transfer list and turns it into the options object the engine's
/// <c>structuredClone</c> understands.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transfer is the one thing that changes the worker's two-clone contract</b>, so it is worth
/// being explicit about what "transfer" means here. The engine's <c>structuredClone</c> implements it
/// as <em>copy the bytes, then detach the source</em> — not as a zero-copy handover. The observable
/// semantics are the spec's (the sender's buffer is detached and unusable afterwards; the receiver
/// has the contents); what is not delivered is the performance reason transferables exist. That is a
/// property of the engine's implementation rather than of this binding, and it is recorded here
/// because "transferred" reads like a promise of zero copies.
/// </para>
/// <para>
/// <b>Only <c>ArrayBuffer</c>.</b> A <c>MessagePort</c> in the list is refused rather than silently
/// copied — porting a port into a worker needs the port's peer to live on the other thread, which is
/// a different piece of work. Refusing is the honest answer; a copy would look like it worked and
/// then deliver messages to nobody.
/// </para>
/// <para>
/// The validation order matches <c>MessagingBinding.ExtractTransferList</c>, which is the same job
/// for same-document messaging: a non-array list, a non-transferable entry, an already-detached
/// buffer and a duplicate entry are each a <c>DataCloneError</c>.
/// </para>
/// <para>
/// <b>Engine-typed, and it is the transferables that pin it rather than the plumbing.</b> Every
/// decision here is a statement about <c>ArrayBuffer</c> — is this one, is it detached — and about
/// the <c>{ transfer: [...] }</c> shape the engine's own <c>structuredClone</c> reads. JSEAL models
/// none of that. What did move is where the error comes from: the sender's realm is now named as
/// whichever of the two it is, because the two senders live in <em>different</em> realms and only one
/// of them has a JSEAL handle on its own. <see cref="WorkerBinding"/> posts from the page, which has
/// a realm; <see cref="JSWorker"/> posts from a context it created itself on its own thread, which
/// JSEAL offers no way to build and therefore no way to name.
/// </para>
/// </remarks>
internal static class WorkerTransfer
{
    /// <summary>
    /// Returns the <c>{ transfer: [...] }</c> options for <c>structuredClone</c>, or
    /// <see cref="JSUndefined.Value"/> when nothing is being transferred. Throws
    /// <c>DataCloneError</c> for an invalid list. For the page-side sender, which has a realm.
    /// </summary>
    /// <param name="realm">The realm the error is raised in — the sender's.</param>
    /// <param name="transferValue">
    /// The second <c>postMessage</c> argument: the transfer array itself, or an options object
    /// carrying a <c>transfer</c> property (the modern spelling <c>structuredClone</c> uses).
    /// </param>
    public static JSValue BuildCloneOptions(IJsRealm realm, JSValue? transferValue) =>
        Build(transferValue, message => throw realm.DomError("DataCloneError", message));

    /// <summary>
    /// The same, for the worker-side sender, whose context it built itself and which therefore has no
    /// JSEAL realm to raise through.
    /// </summary>
    /// <param name="context">The context the error is raised in — the worker's own.</param>
    /// <param name="transferValue">The second <c>postMessage</c> argument.</param>
    public static JSValue BuildCloneOptions(JSContext context, JSValue? transferValue) =>
        Build(transferValue, message => DomBridge.ThrowDOMException(context, message, "DataCloneError"));

    /// <summary>
    /// The validation itself, with the one thing the two senders disagree about — how a
    /// <c>DataCloneError</c> is raised — handed in.
    /// </summary>
    /// <remarks>
    /// <paramref name="raiseDataCloneError"/> always throws. The <c>return</c> after each call is
    /// there because the compiler cannot see that through a delegate, and it is why the shape of this
    /// method is unchanged from when it called <c>ThrowDOMException</c> directly.
    /// </remarks>
    private static JSValue Build(JSValue? transferValue, Action<string> raiseDataCloneError)
    {
        if (transferValue is null || transferValue.IsNullOrUndefined)
            return JSUndefined.Value;

        // postMessage(msg, [buf]) and postMessage(msg, { transfer: [buf] }) are both accepted; the
        // second is what structuredClone itself takes, and page code written against either spelling
        // should not silently transfer nothing.
        var list = transferValue as JSArray;
        if (list is null && transferValue is JSObject options && options[(KeyString)"transfer"] is JSArray nested)
            list = nested;

        if (list is null)
        {
            raiseDataCloneError("The transfer list contains a non-transferable value.");
            return JSUndefined.Value;
        }

        var buffers = new List<JSValue>();
        var seen = new HashSet<JSArrayBuffer>(ReferenceEqualityComparer.Instance);

        foreach (var (_, item) in list.GetArrayElements(withHoles: false))
        {
            if (item is not JSArrayBuffer buffer)
            {
                raiseDataCloneError("The transfer list contains a non-transferable value.");
                return JSUndefined.Value;
            }

            if (buffer.Detached)
            {
                raiseDataCloneError("The transfer list contains a detached ArrayBuffer.");
                return JSUndefined.Value;
            }

            if (!seen.Add(buffer))
            {
                raiseDataCloneError("The transfer list contains duplicate transferable values.");
                return JSUndefined.Value;
            }

            buffers.Add(buffer);
        }

        if (buffers.Count == 0)
            return JSUndefined.Value;

        var cloneOptions = new JSObject();
        cloneOptions.FastAddValue("transfer", new JSArray(buffers), JSPropertyAttributes.EnumerableConfigurableValue);
        return cloneOptions;
    }
}
