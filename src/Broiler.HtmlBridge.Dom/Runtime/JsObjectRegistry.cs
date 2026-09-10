using System.Runtime.CompilerServices;

using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single authority for JavaScript wrapper identity (HtmlBridge complexity-reduction roadmap
/// Phase 2, P2.2). A DOM node must map to exactly one <see cref="JsValue"/> wrapper for the life
/// of a document so that script identity holds (<c>node === node</c>, listeners registered on a
/// wrapper are found again, <c>Map</c>/<c>Set</c> keys are stable). This registry owns the two
/// maps that used to be scattered bridge fields — the per-node wrapper cache and the
/// sub-document-root document wrapper cache — behind one narrow surface.
/// </summary>
/// <remarks>
/// <para>
/// Wrappers are keyed by reference identity (a DOM node's identity is its object identity), never
/// by value, so a node whose contents change keeps its wrapper. Instance-scoped to the owning
/// bridge/document; <see cref="Clear"/> runs on re-parse and disposal. Not thread-safe — wrapper
/// creation happens on the document thread (Phase 2's P2.4 defines that threading model).
/// </para>
/// <para>
/// <b>It moved in one commit, because it could not move in any other number.</b> Every one of its
/// members had a caller outside its own group holding the engine's object mid-call, so re-typing the
/// surface and re-typing the callers are the same change: <c>ApplyInterfacePrototype</c> and
/// <c>ElementContentBinding.InstallTextContent</c> each took the engine's object and each had one or
/// two call sites, and the interface members that reach a node from their receiver had a handle
/// already and were unwrapping it to ask. That is why this was the last table to cross rather than
/// the first, and why the step is one commit rather than a file-by-file migration - the opposite of
/// what the incremental seam is for, and correct here for that reason.
/// </para>
/// <para>
/// <b>The <c>ConditionalWeakTable</c> keys on <see cref="JsValue.ObjectIdentity"/>, and a paragraph
/// once stood here saying it could not.</b> It said "a weak table needs a <em>reference</em> key and
/// a <c>JsValue</c> is a struct, so this table stays keyed on the engine object however far the rest
/// of the bridge moves", and called that the clearest single measure of what the handle design costs.
/// The struct was never the key: the reference a handle carries is, and a provider is already
/// required to make it canonical per guest object because handle equality is defined by it. The
/// weakness is unchanged - the identity lives exactly as long as the guest object does.
/// </para>
/// </remarks>
internal sealed class JsObjectRegistry
{
    private readonly Dictionary<DomNode, JsValue> _nodeWrappers = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The same pairs the other way round, so a wrapper can name its node in constant time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A member that lives on an interface prototype has no node captured in a closure — it finds one
    /// from its receiver, on every call (see <c>DomBridge.CharacterDataInterface.cs</c>). The scan
    /// <see cref="TryGetNode"/> used to do was fine for the handful of call sites that had it, and is
    /// not fine per DOM operation: it is linear in the wrappers the document has minted, so a
    /// prototype method would have cost more the larger the page.
    /// </para>
    /// <para>
    /// <b>Weakly keyed, and outliving <see cref="Remove"/> deliberately.</b> A wrapper the page still
    /// holds must keep naming its node after the node leaves the tree, because in a browser a removed
    /// node goes on working — <c>var gone = host.firstChild; host.innerHTML = '…'; gone.tagName</c>
    /// still answers. This map is what an inherited member reads, so dropping the entry with the
    /// forward one turned every such member into an illegal invocation while the members the wrapper
    /// still owned kept working. The weak key is what keeps that from being a leak: the node is held
    /// only for as long as script can still reach it through the wrapper, where the forward map holds
    /// both outright and is the one <see cref="Remove"/> releases.
    /// </para>
    /// </remarks>
    private readonly ConditionalWeakTable<object, DomNode> _wrapperNodes = new();
    // Phase 4 item 1 (P4.4a): keyed by DomNode so a canonical DomDocument browsing-context root maps
    // to its document wrapper, alongside the legacy #subdoc-root element roots.
    private readonly Dictionary<DomNode, JsValue> _documentWrappers = new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets the wrapper already registered for <paramref name="node"/>, if any.</summary>
    public bool TryGet(DomNode node, out JsValue wrapper) => _nodeWrappers.TryGetValue(node, out wrapper);

    /// <summary>
    /// Registers <paramref name="wrapper"/> as the identity of <paramref name="node"/>. Callers
    /// register the (empty) wrapper before populating it so re-entrant lookups during population
    /// resolve to the same instance.
    /// </summary>
    public void Set(DomNode node, JsValue wrapper)
    {
        var identity = IdentityOf(wrapper);

        // Re-registering a node under a new wrapper must not leave the old one naming it. The
        // comparison is handle equality, which for an object kind IS reference identity of the
        // reference the handle carries - the same question `ReferenceEquals` asked of the engine's
        // object before.
        if (_nodeWrappers.TryGetValue(node, out var previous) && previous != wrapper)
            _wrapperNodes.Remove(IdentityOf(previous));

        _nodeWrappers[node] = wrapper;
        _wrapperNodes.AddOrUpdate(identity, node);
    }

    /// <summary>The identity this registry's weak map keys on.</summary>
    /// <remarks>
    /// See <see cref="JsValue.ObjectIdentity"/>. It throws rather than tolerating a non-object
    /// because a wrapper that is not an object is a bridge defect, and the engine-typed surface this
    /// replaced could not express one at all.
    /// </remarks>
    private static object IdentityOf(JsValue wrapper) =>
        wrapper.ObjectIdentity ?? throw new InvalidOperationException(
            "the wrapper registry was given a handle that is not an object");

    /// <summary>
    /// Drops <paramref name="node"/>'s wrapper (e.g. when the node is removed/adopted away), so
    /// neither is held by this registry any longer.
    /// </summary>
    /// <remarks>
    /// The reverse entry is left in place on purpose: it is weakly keyed, so it holds the node only
    /// while script can still reach the wrapper, and that is exactly the case a browser keeps working
    /// — a node removed from the tree answers its members as before. See <see cref="_wrapperNodes"/>.
    /// </remarks>
    public bool Remove(DomNode node) => _nodeWrappers.Remove(node);

    /// <summary>The registered node→wrapper pairs, for the reverse-lookup call sites.</summary>
    public IEnumerable<KeyValuePair<DomNode, JsValue>> Entries => _nodeWrappers;

    /// <summary>
    /// Finds the node whose wrapper is <paramref name="wrapper"/> (reverse lookup by reference
    /// identity), in constant time.
    /// </summary>
    public bool TryGetNode(JsValue wrapper, out DomNode node)
    {
        // A non-object handle is simply not in the map, rather than a defect: this is asked of a
        // receiver, and a page may call an inherited member on anything.
        if (wrapper.ObjectIdentity is { } identity)
            return _wrapperNodes.TryGetValue(identity, out node!);

        node = null!;
        return false;
    }

    /// <summary>
    /// Registers the <c>document</c> wrapper for a sub-document root (<c>#subdoc-root</c>). The root
    /// element itself is also registered as a normal node wrapper by the caller; this second map
    /// answers "the document object owning this root".
    /// </summary>
    public void SetDocument(DomNode documentRoot, JsValue document) => _documentWrappers[documentRoot] = document;

    /// <summary>Gets the <c>document</c> wrapper registered for a sub-document root, if any.</summary>
    public bool TryGetDocument(DomNode documentRoot, out JsValue document) =>
        _documentWrappers.TryGetValue(documentRoot, out document);

    /// <summary>Drops every wrapper identity — both node and sub-document maps. Called on re-parse and disposal.</summary>
    public void Clear()
    {
        _nodeWrappers.Clear();
        _wrapperNodes.Clear();
        _documentWrappers.Clear();
    }
}
