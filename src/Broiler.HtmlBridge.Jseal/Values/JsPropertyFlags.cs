namespace Broiler.HtmlBridge.Jseal;

/// <summary>
/// The property attributes a member is installed with.
/// </summary>
/// <remarks>
/// <para>
/// The DOM bridge installs 1,246 members and uses exactly four combinations of Broiler.JS's
/// <c>JSPropertyAttributes</c> to do it: <c>EnumerableConfigurableValue</c> (908),
/// <c>EnumerableConfigurableProperty</c> (331), <c>ConfigurableValue</c> (6) and
/// <c>ConfigurableProperty</c> (1). Nothing is ever installed read-only, non-configurable, or with an
/// explicit writable flag, and read-only is expressed by passing a null setter at 216 sites.
/// </para>
/// <para>
/// So this enum carries the three WebIDL-relevant bits and nothing else. The value/accessor
/// distinction is <b>not</b> a flag here — it is which method you call, <see cref="IJsRealm.DefineValue"/>
/// or <see cref="IJsRealm.DefineAccessor"/>. Encoding it as a flag is what let the two disagree in the
/// first place: a value installed with the accessor bit set, or the reverse, is a mistake the
/// compiler cannot catch, and an engine whose property storage separates the two (most do) has to
/// re-derive the answer the caller already knew.
/// </para>
/// </remarks>
[Flags]
public enum JsPropertyFlags : byte
{
    /// <summary>Not enumerable, not configurable, not writable.</summary>
    None = 0,

    /// <summary>Appears in <c>for…in</c> and <c>Object.keys</c>.</summary>
    Enumerable = 1,

    /// <summary>May be redefined or deleted.</summary>
    Configurable = 2,

    /// <summary>
    /// May be assigned to. Meaningful only for a value property; an accessor's writability is whether
    /// it has a setter.
    /// </summary>
    Writable = 4,

    /// <summary>
    /// What almost every DOM member is: enumerable, configurable, and — for a value property —
    /// writable. This is the WebIDL default for an operation or an attribute on an interface.
    /// </summary>
    Default = Enumerable | Configurable | Writable,

    /// <summary>
    /// Configurable but not enumerable — what <c>Storage</c>'s five methods, its <c>length</c>, and
    /// <c>PerformanceObserver.prototype</c> use, and the only other combination the bridge needs.
    /// </summary>
    NonEnumerable = Configurable | Writable,
}
