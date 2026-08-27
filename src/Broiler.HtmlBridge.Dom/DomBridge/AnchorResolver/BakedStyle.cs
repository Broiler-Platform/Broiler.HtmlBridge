using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    /// <summary>
    /// Per-element <b>baked-style overlay</b> (Phase 4 item 2, increment 3): the store for the bridge's
    /// serialize-time bake writers (anchor resolver, animation-snapshot resolver, synthetic form-control
    /// styling, zoom bake), split out of the script-observable inline-style dict (<see cref="InlineStyle"/>).
    /// A <c>null</c> value is a <b>tombstone</b> — a bake resolver removing an authored/prior property (e.g.
    /// the animation resolver dropping the <c>animation</c> shorthand after baking longhands, or the anchor
    /// resolver dropping <c>margin</c> after baking <c>margin-*</c>) so the merged view excludes it.
    /// GC-scoped to the element (a detached element's overlay is collected with it).
    /// </summary>
    private readonly ConditionalWeakTable<DomElement, Dictionary<string, string?>> _bakedStyleOverlays = new();

    private Dictionary<string, string?> BakedStyleOverlayFor(DomElement element) =>
        _bakedStyleOverlays.GetValue(element, static _ => new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The seam onto an element's inline-style working store for the bridge's <b>serialize-time bake
    /// writers</b> — the anchor resolver (position/insets/margins/borders/size), the animation-snapshot
    /// resolver (interpolated keyframe values), synthetic form-control styling (meter/progress) and the
    /// zoom serialization bake. Each reads the element's effective inline style and writes resolved/baked
    /// values, as the passes build on one another.
    ///
    /// HtmlBridge complexity-reduction roadmap Phase 4 item 2 (inline-style single authority). Increment 1
    /// routed the anchor-resolver cluster (~98 sites) here; increment 2 routed the remaining bridge-internal
    /// bake writers; increment 3 (this) backs the writes with a store distinct from the script-observable
    /// inline style: writes land in the per-element <see cref="BakedStyleOverlayFor">baked overlay</see>,
    /// and reads return the <b>merged</b> base ∪ overlay view (overlay wins, tombstones remove). The script
    /// path (<c>element.style</c>/<c>setAttribute</c>, which write-throughs to the <c>style=</c> attribute),
    /// the cascade cleanup and parse-time authored-style copy stay on <see cref="InlineStyle"/>. Because the
    /// bakes are strictly terminal (serialize-time, after all script/cascade writes), applying the overlay
    /// last reproduces the old single-dict content exactly, so serialized output is byte-identical — while
    /// <c>InlineStyleRuntimeState.Style</c> is no longer polluted by bakes. The serializer's own effective-
    /// style reads (<see cref="EffectiveInlineStyle"/>) are the merge point.
    /// </summary>
    internal BakedStyleMap BakedInlineStyle(DomElement element) =>
        new(InlineStyle(element), BakedStyleOverlayFor(element));

    /// <summary>
    /// The materialized <b>merged</b> inline style for a serialize-time reader: the base
    /// <see cref="InlineStyle"/> dict with the element's <see cref="BakedStyleOverlayFor">baked overlay</see>
    /// applied (overlay values win; <c>null</c>-tombstoned keys removed). This is what the serializer emits
    /// and what the <c>style=</c> attribute is synced from, so the baked geometry reaches the renderer.
    /// Returns a fresh dictionary (safe to enumerate/order). For elements with no bakes the overlay is
    /// empty and this equals the base dict.
    /// </summary>
    internal Dictionary<string, string> EffectiveInlineStyle(DomElement element)
    {
        // Read-only (the result is a fresh dictionary), so take the non-bumping accessor — see
        // SerializeInlineStyleForEngine for why a read must not invalidate the geometry snapshot.
        var baseStyle = InlineStyleForRead(element);
        if (!_bakedStyleOverlays.TryGetValue(element, out var overlay) || overlay.Count == 0)
            return new Dictionary<string, string>(baseStyle, StringComparer.OrdinalIgnoreCase);

        var merged = new Dictionary<string, string>(baseStyle, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in overlay)
        {
            if (value is null)
                merged.Remove(key);
            else
                merged[key] = value;
        }
        return merged;
    }

    // Phase 4 item 5 hook (CopyBridgeRuntimeStateTo): copy a source element's baked overlay onto its
    // cloneNode copy, so a clone made after a bake carries the same resolved geometry (byte-identical with
    // the pre-split behaviour, where bakes lived in the copied inline-style dict). A no-op when the source
    // has no overlay (the common case — clones are made by script, before serialize-time baking).
    private void CopyBakedStyleOverlay(DomElement source, DomElement clone)
    {
        if (!_bakedStyleOverlays.TryGetValue(source, out var sourceOverlay) || sourceOverlay.Count == 0)
            return;
        var cloneOverlay = BakedStyleOverlayFor(clone);
        cloneOverlay.Clear();
        foreach (var (key, value) in sourceOverlay)
            cloneOverlay[key] = value;
    }
}

/// <summary>
/// Value wrapper the bridge's serialize-time bake writers use (returned by
/// <see cref="DomBridge.BakedInlineStyle"/>). Writes land in the per-element baked <paramref name="overlay"/>
/// (indexer set → value; <c>Remove</c> → <c>null</c> tombstone); reads return the <b>merged</b>
/// base ∪ overlay view (overlay wins, tombstones remove). It mirrors the <see cref="Dictionary{TKey,TValue}"/>
/// surface the cluster uses (indexer get/set, <c>Remove</c>, <c>ContainsKey</c>, <c>TryGetValue</c>,
/// <c>GetValueOrDefault</c>, <c>Keys</c>, <c>Count</c>, enumeration). Enumeration/<c>Keys</c>/<c>Count</c>
/// materialize the merge; the per-key reads do not.
/// </summary>
internal readonly struct BakedStyleMap
{
    private readonly Dictionary<string, string> _base;
    private readonly Dictionary<string, string?> _overlay;

    public BakedStyleMap(Dictionary<string, string> baseStyle, Dictionary<string, string?> overlay)
    {
        _base = baseStyle;
        _overlay = overlay;
    }

    public string this[string key]
    {
        get => _overlay.TryGetValue(key, out var ov)
            ? ov ?? throw new KeyNotFoundException(key)
            : _base[key];
        set => _overlay[key] = value;
    }

    // Tombstone the key so the merged view excludes it (whether it came from base or a prior overlay write).
    // Returns whether the key was present in the merged view beforehand, matching Dictionary.Remove.
    public bool Remove(string key)
    {
        var wasPresent = ContainsKey(key);
        _overlay[key] = null;
        return wasPresent;
    }

    public bool ContainsKey(string key) =>
        _overlay.TryGetValue(key, out var ov) ? ov is not null : _base.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value)
    {
        if (_overlay.TryGetValue(key, out var ov))
        {
            value = ov;
            return ov is not null;
        }
        return _base.TryGetValue(key, out value);
    }

    public string? GetValueOrDefault(string key) => TryGetValue(key, out var value) ? value : null;

    public int Count => Materialize().Count;

    public IEnumerable<string> Keys => Materialize().Keys;

    public Dictionary<string, string>.Enumerator GetEnumerator() => Materialize().GetEnumerator();

    private Dictionary<string, string> Materialize()
    {
        var merged = new Dictionary<string, string>(_base, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in _overlay)
        {
            if (value is null)
                merged.Remove(key);
            else
                merged[key] = value;
        }
        return merged;
    }
}
