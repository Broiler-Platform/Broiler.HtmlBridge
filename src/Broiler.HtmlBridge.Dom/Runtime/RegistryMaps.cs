namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The one map operation the runtime registries all need and none of them gets from
/// <see cref="Dictionary{TKey, TValue}"/>: read the entry for a key, creating it on first access.
/// </summary>
/// <remarks>
/// Each registry wrote this out as a <c>TryGetValue</c>, a construction and an assignment, so the
/// shape was repeated once per store while the interesting part — <em>what</em> gets created, which
/// differs at every site down to the comparer the created map is given — sat in the middle of it.
/// The factory keeps that part at the call site: nothing here decides what an absent entry becomes.
/// </remarks>
internal static class RegistryMaps
{
    /// <summary>
    /// The value <paramref name="map"/> holds for <paramref name="key"/>, adding
    /// <paramref name="factory"/>'s result first when there is none.
    /// </summary>
    /// <remarks>
    /// The factory runs only on a miss, exactly as the hand-written blocks did — a registry whose
    /// entry already exists allocates nothing.
    /// </remarks>
    internal static TValue GetOrAdd<TKey, TValue>(
        Dictionary<TKey, TValue> map, TKey key, Func<TValue> factory)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var value))
        {
            value = factory();
            map[key] = value;
        }

        return value;
    }
}
