namespace Broiler.HtmlBridge.Jseal.Vm;

/// <summary>
/// <see cref="IJsJobs"/>: the microtask queue, and the promises this engine cannot yet hand a host.
/// </summary>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public bool HasPendingJobs => InStep(realm => realm.HasPendingJobs);

    /// <inheritdoc />
    public void EnqueueJob(Action job)
    {
        ArgumentNullException.ThrowIfNull(job);

        InStep(realm => realm.EnqueueJob(job));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>A job queued by a job is followed, which is why there is a limit.</b> The count answered
    /// is the number that ran, so a caller draining with a limit can tell an exhausted queue from a
    /// truncated one by asking <see cref="HasPendingJobs"/> afterwards.
    /// </remarks>
    public int DrainJobs(int limit = 10_000) => InStep(realm => realm.DrainJobs(limit));

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>Not provided, and refused rather than approximated.</b> The Broiler.VM profile has
    /// promises and a job queue, and what it has no seam for is the pair of functions that settle
    /// one from outside the guest - which is the whole of what this member is. A provider could
    /// fake it by evaluating a snippet that captures the resolvers into a host object, and that
    /// would work until a page whose Content-Security-Policy forbids evaluation asked for a
    /// <c>fetch</c>: the promise the bridge needs would depend on the capability the page just
    /// refused.
    /// </para>
    /// <para>
    /// So the capability is not declared and this refuses, which is the contract's own answer for a
    /// host that did not branch on <see cref="Capabilities"/>.
    /// </para>
    /// </remarks>
    public JsValue NewPromise(out Action<JsValue> resolve, out Action<JsValue> reject)
    {
        resolve = static _ => { };
        reject = static _ => { };

        throw Lacking(JsCapabilities.Promises);
    }
}
