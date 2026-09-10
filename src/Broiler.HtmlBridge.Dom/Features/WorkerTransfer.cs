using System;
using System.Collections.Generic;
using System.Globalization;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Validates a <c>postMessage</c> transfer list and turns it into the list of transferable objects a
/// structured clone detaches.
/// </summary>
/// <remarks>
/// <para>
/// <b>Transfer is the one thing that changes the worker's two-clone contract</b>, so it is worth
/// being explicit about what "transfer" means here. The engine's structured clone implements it as
/// <em>copy the bytes, then detach the source</em> — not as a zero-copy handover. The observable
/// semantics are the spec's (the sender's buffer is detached and unusable afterwards; the receiver
/// has the contents); what is not delivered is the performance reason transferables exist. That is a
/// property of the engine's implementation rather than of this binding, and it is recorded here
/// because "transferred" reads like a promise of zero copies.
/// </para>
/// <para>
/// <b>Only the engine's own transferables.</b> A <c>MessagePort</c> in the list is refused rather
/// than silently copied — porting a port into a worker needs the port's peer to live on the other
/// thread, which is a different piece of work. Refusing is the honest answer; a copy would look like
/// it worked and then deliver messages to nobody. (Same-document messaging <em>does</em> carry ports,
/// and <see cref="MessagingBinding"/> classifies them itself before asking the realm about anything
/// it did not recognise.)
/// </para>
/// <para>
/// The validation order matches <c>MessagingBinding.ExtractTransferList</c>, which is the same job
/// for same-document messaging: a non-array list, a non-transferable entry, an already-detached
/// buffer and a duplicate entry are each a <c>DataCloneError</c>.
/// </para>
/// <para>
/// <b>It named the engine until <see cref="IJsClone"/> existed, and what it named was the
/// transferables rather than the plumbing.</b> Every decision here used to be a statement about
/// <c>ArrayBuffer</c> — is this one, is it detached — and about the <c>{ transfer: [...] }</c> shape
/// the engine's own <c>structuredClone</c> reads. The first is now
/// <see cref="IJsClone.ClassifyTransferable"/>, which asks the question in the specification's
/// vocabulary (is this transferable, is it spent) rather than in an engine's; the second belongs to
/// the provider, because the options object is the clone's own signature and no host has business
/// building one. The two senders that were told apart by which of them had a realm are now one
/// method, because the worker's context is a realm too.
/// </para>
/// </remarks>
internal static class WorkerTransfer
{
    /// <summary>
    /// Whether this realm's engine can structured-clone at all, which every caller of
    /// <see cref="IJsClone"/> has to ask before it asks anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Asking is the contract, and a <c>catch</c> is not a substitute for it.</b> A realm that
    /// lacks <see cref="JsCapabilities.WorkerRealms"/> answers <c>Clone</c>, <c>Detach</c> and
    /// <c>Adopt</c> with <c>JsCapabilityUnavailableException</c>, which is deliberately NOT a
    /// <c>JsEngineException</c>: the second means the page's code went wrong and the first means the
    /// host's did. <c>JsErrors.cs</c> states that distinction and its reason -- "A host that branches
    /// on IJsRealm.Capabilities never sees it, which is the point -- it is the backstop for a call
    /// site that forgot to."
    /// </para>
    /// <para>
    /// Five call sites forgot to, and each sat inside <c>catch (JsEngineException)</c> that could
    /// never fire for it, so on a provider without the capability the backstop escaped raw to page
    /// script instead of the <c>DataCloneError</c> the catch was written to produce. The fix is this
    /// question rather than a wider catch, because widening one would make a host bug and a page
    /// error indistinguishable everywhere -- which is the thing the two types exist to tell apart.
    /// </para>
    /// <para>
    /// <b>What it does not decide.</b> Whether <c>Worker</c> and <c>MessageChannel</c> should be
    /// absent altogether on such an engine, rather than present and refusing, is a real question and
    /// a larger one: feature detection would have a page take a different path instead of catching a
    /// <c>DataCloneError</c>. Refusing is what this repository does elsewhere for a capability it
    /// lacks, and changing that is not this fix.
    /// </para>
    /// </remarks>
    internal static bool CanStructuredClone(IJsRealm realm) =>
        (realm.Capabilities & JsCapabilities.WorkerRealms) != 0;

    /// <summary>
    /// What a <c>DataCloneError</c> says when nothing could have been cloned -- distinct from the
    /// message for a value that is merely uncloneable, because the cause is the engine and not the
    /// argument.
    /// </summary>
    internal const string EngineCannotCloneMessage =
        "The object could not be cloned: this JavaScript engine does not implement structured clone.";

    /// <summary>
    /// The transferable objects named by a <c>postMessage</c> transfer argument, ready to hand to
    /// <see cref="IJsClone.Clone"/> or <see cref="IJsClone.Detach"/>. Empty when nothing is being
    /// transferred; throws <c>DataCloneError</c> for an invalid list.
    /// </summary>
    /// <param name="realm">The sender's realm — the one the error is raised in, and the one that
    /// classifies an entry.</param>
    /// <param name="transferValue">
    /// The second <c>postMessage</c> argument: the transfer array itself, or an options object
    /// carrying a <c>transfer</c> property (the modern spelling <c>structuredClone</c> uses).
    /// </param>
    public static JsValue[] BuildTransferList(IJsRealm realm, JsValue transferValue)
    {
        if (transferValue.IsNullish)
            return [];

        // postMessage(msg, [buf]) and postMessage(msg, { transfer: [buf] }) are both accepted; the
        // second is what structuredClone itself takes, and page code written against either spelling
        // should not silently transfer nothing. The array is tested first because an array is also an
        // object, and testing the other way round would look inside one for a `transfer` member.
        var list = transferValue;
        if (!list.IsArray)
        {
            var nested = transferValue.IsObject ? realm.GetProperty(transferValue, "transfer") : JsValue.Missing;
            if (!nested.IsArray)
                throw realm.DomError("DataCloneError", "The transfer list contains a non-transferable value.");

            list = nested;
        }

        var buffers = new List<JsValue>();
        var seen = new HashSet<JsValue>();

        foreach (var item in ArrayElements(realm, list))
        {
            switch (realm.ClassifyTransferable(item))
            {
                case JsTransferKind.Detached:
                    throw realm.DomError("DataCloneError", "The transfer list contains a detached ArrayBuffer.");

                case JsTransferKind.Transferable when !seen.Add(item):
                    throw realm.DomError("DataCloneError", "The transfer list contains duplicate transferable values.");

                case JsTransferKind.Transferable:
                    buffers.Add(item);
                    break;

                default:
                    throw realm.DomError("DataCloneError", "The transfer list contains a non-transferable value.");
            }
        }

        return [.. buffers];
    }

    /// <summary>
    /// The elements an Array actually has, in index order, with holes skipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is <c>GetArrayElements(withHoles: false)</c>, which is what both transfer-list walks used
    /// and which a length-and-index walk would get wrong: a hole would arrive as a value that is not
    /// transferable and turn <c>postMessage(m, [ , buf])</c> into a <c>DataCloneError</c> that a
    /// browser does not raise.
    /// </para>
    /// <para>
    /// <see cref="IJsMembers.OwnPropertyNames"/> is the engine-neutral spelling of the same question —
    /// an array's present elements are its own enumerable index-keyed properties, and a hole is the
    /// absence of one. The numeric filter is what keeps a named property somebody hung on the array
    /// out of a list of elements; <c>length</c> is not enumerable and never appears.
    /// </para>
    /// </remarks>
    internal static IEnumerable<JsValue> ArrayElements(IJsRealm realm, JsValue array)
    {
        foreach (var name in realm.OwnPropertyNames(array))
        {
            // NumberStyles.None so that only a canonical decimal index counts: a property literally
            // called " 1" or "+1" is a named member, not element one, and the permissive default
            // would quietly promote it.
            if (uint.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                yield return realm.GetIndex(array, index);
        }
    }
}
