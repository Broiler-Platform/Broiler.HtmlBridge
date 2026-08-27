using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's registered <c>MutationObserver</c>s (HtmlBridge
/// complexity-reduction roadmap Phase 2, P2.5): each observer's JS object, its observed target and
/// its options. It replaces the bare observer list the bridge kept, giving one place to register,
/// unregister and enumerate observers when a mutation is delivered.
/// </summary>
/// <remarks>
/// The bridge still builds and delivers the JS mutation records (it needs the JS object model and
/// tree), reading the observer set from here via <see cref="Snapshot"/>. Instance-scoped to the
/// owning bridge/document; <see cref="Clear"/> runs on re-parse and disposal.
/// </remarks>
internal sealed class MutationObserverHub
{
    private readonly List<(JSObject Observer, DomNode Target, DomMutationObserverOptions Options)> _observers = [];

    /// <summary>The number of registered observers (a fast pre-check before building a record).</summary>
    public int Count => _observers.Count;

    /// <summary>
    /// Registers <paramref name="observer"/> for <paramref name="target"/>. A repeat
    /// <c>observe()</c> for the same observer+target replaces the prior options (matching
    /// <c>MutationObserver.observe</c>).
    /// </summary>
    public void Register(JSObject observer, DomNode target, DomMutationObserverOptions options)
    {
        _observers.RemoveAll(entry =>
            ReferenceEquals(entry.Observer, observer) &&
            ReferenceEquals(entry.Target, target));
        _observers.Add((observer, target, options));
    }

    /// <summary>Removes every registration for <paramref name="observer"/> (its <c>disconnect()</c>).</summary>
    public void Unregister(JSObject observer) =>
        _observers.RemoveAll(entry => ReferenceEquals(entry.Observer, observer));

    /// <summary>A snapshot of the registrations, safe to iterate while delivery mutates the set.</summary>
    public (JSObject Observer, DomNode Target, DomMutationObserverOptions Options)[] Snapshot() => _observers.ToArray();

    /// <summary>Drops every registration.</summary>
    public void Clear() => _observers.Clear();
}
