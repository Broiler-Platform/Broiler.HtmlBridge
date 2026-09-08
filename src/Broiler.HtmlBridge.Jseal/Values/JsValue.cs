using System.Globalization;

namespace Broiler.HtmlBridge.Jseal;

/// <summary>
/// A JavaScript value, as the host sees it: a tag, an inline number, and — for anything the engine
/// owns — an opaque reference to the engine's own value.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a struct rather than an interface hierarchy.</b> The obvious shape for this is
/// <c>IJsValue</c> with <c>IJsObject</c>/<c>IJsFunction</c> beneath it, implemented by provider types
/// that derive from the engine's own — and for objects that is exactly what happens (see
/// <see cref="Reference"/>). It cannot be the shape for primitives, because Broiler.JS's
/// <c>JSNumber</c> is <see langword="sealed"/>: a provider cannot derive from it, so an interface
/// would force a carrier object per number, allocated on a path that already runs per property read.
/// A three-field struct carries a number without allocating and an object without wrapping, which is
/// the only shape that is cheap for both.
/// </para>
/// <para>
/// The layout — tag plus <see cref="double"/> plus reference, 24 bytes — is not invented here. It is
/// what Broiler.VM's own <c>JsValue</c> chose, for the reason it records: the collector is the CLR's,
/// so a value that must sometimes hold a managed reference cannot be a NaN-boxed word. Matching it
/// means a future Broiler.VM provider re-tags rather than converts.
/// </para>
/// <para>
/// <b>What <see cref="Reference"/> holds is the engine's value, not a wrapper.</b> A provider mints an
/// object handle over the object the engine already has. Under the Broiler.JS provider that is a
/// <c>JSObject</c>, so the seven <c>ConditionalWeakTable</c>s the bridge keys on wrapper identity keep
/// keying on the same instances they always did, and <c>el === el</c> holds because it is the same
/// question it was before. A provider that cannot do this — one over a C API whose values are
/// pointers or stack slots — is expected to canonicalise handles itself; see
/// <c>docs/jseal.md</c>.
/// </para>
/// </remarks>
public readonly struct JsValue : IEquatable<JsValue>
{
    private readonly object? _reference;
    private readonly double _number;
    private readonly JsValueKind _kind;

    private JsValue(JsValueKind kind, double number, object? reference)
    {
        _kind = kind;
        _number = number;
        _reference = reference;
    }

    /// <summary>What this value holds.</summary>
    public JsValueKind Kind => _kind;

    /// <summary>
    /// The engine's own value, for a value the engine owns; <see langword="null"/> for a primitive
    /// this struct carries inline and for <see cref="Missing"/>.
    /// </summary>
    /// <remarks>
    /// Only a provider has any business reading this, and only after checking that the value came
    /// from the realm it is about to be handed back to. It is exposed rather than internal so a
    /// provider can live outside this repository; <see cref="Providers.JsProviderValue"/> is the
    /// matching mint side.
    /// </remarks>
    public object? Reference => _reference;

    /// <summary>No value was supplied. The default, so <c>default(JsValue)</c> means it.</summary>
    public static JsValue Missing => default;

    /// <summary>The <c>undefined</c> value.</summary>
    public static JsValue Undefined => new(JsValueKind.Undefined, 0d, null);

    /// <summary>The <c>null</c> value.</summary>
    public static JsValue Null => new(JsValueKind.Null, 0d, null);

    /// <summary>The <c>true</c> value.</summary>
    public static JsValue True => new(JsValueKind.Boolean, 1d, null);

    /// <summary>The <c>false</c> value.</summary>
    public static JsValue False => new(JsValueKind.Boolean, 0d, null);

    /// <summary>A number.</summary>
    public static JsValue Number(double value) => new(JsValueKind.Number, value, null);

    /// <summary>A boolean.</summary>
    public static JsValue Boolean(bool value) => value ? True : False;

    /// <summary>
    /// A string. A CLR <see langword="null"/> becomes <see cref="Null"/> rather than a string, because
    /// every bridge site that produces one is reflecting an absent DOM value and that is what the DOM
    /// says such a value is.
    /// </summary>
    public static JsValue String(string? value) =>
        value is null ? Null : new(JsValueKind.String, 0d, value);

    /// <summary>Whether this is <see cref="Missing"/> — no value supplied.</summary>
    public bool IsMissing => _kind == JsValueKind.Missing;

    /// <summary>Whether this is <c>undefined</c>. <see cref="Missing"/> is <b>not</b> undefined; see <see cref="IsNullish"/>.</summary>
    public bool IsUndefined => _kind == JsValueKind.Undefined;

    /// <summary>Whether this is <c>null</c>.</summary>
    public bool IsNull => _kind == JsValueKind.Null;

    /// <summary>
    /// Whether this is <c>null</c>, <c>undefined</c>, or absent — the question the bridge's 21
    /// <c>IsNullOrUndefined</c> sites are asking, which in every one of them is reached from an
    /// argument that may not have been passed at all.
    /// </summary>
    public bool IsNullish => _kind <= JsValueKind.Null;

    /// <summary>Whether this is an object of any kind, including a function or an array.</summary>
    public bool IsObject => _kind >= JsValueKind.Object;

    /// <summary>Whether this is callable.</summary>
    public bool IsFunction => _kind == JsValueKind.Function;

    /// <summary>Whether this is an Array exotic object.</summary>
    public bool IsArray => _kind == JsValueKind.Array;

    /// <summary>Whether this is a string.</summary>
    public bool IsString => _kind == JsValueKind.String;

    /// <summary>Whether this is a number.</summary>
    public bool IsNumber => _kind == JsValueKind.Number;

    /// <summary>Whether this is a boolean.</summary>
    public bool IsBoolean => _kind == JsValueKind.Boolean;

    /// <summary>
    /// This value as a number, without calling into the engine: a number is itself, a boolean is 1 or
    /// 0, and everything else — including a string — is <see cref="double.NaN"/>.
    /// </summary>
    /// <remarks>
    /// This is <b>not</b> ToNumber. A string that looks like a number answers NaN here, because
    /// coercing it is an engine operation that can run user code through <c>valueOf</c> and this
    /// property cannot. Use <see cref="IJsRealm.ToNumber"/> when the ECMAScript coercion is what is
    /// wanted; the bridge's 152 <c>DoubleValue</c> sites are split between the two by whether the
    /// value came from a place that can be a string.
    /// </remarks>
    public double AsNumber => _kind switch
    {
        JsValueKind.Number or JsValueKind.Boolean => _number,
        _ => double.NaN,
    };

    /// <summary>
    /// This value as a boolean, using ECMAScript truthiness, without calling into the engine. Every
    /// object is truthy, so no engine call is needed for any kind.
    /// </summary>
    public bool AsBoolean => _kind switch
    {
        JsValueKind.Missing or JsValueKind.Undefined or JsValueKind.Null => false,
        JsValueKind.Boolean => _number != 0d,
        JsValueKind.Number => _number != 0d && !double.IsNaN(_number),
        JsValueKind.String => ((string)_reference!).Length != 0,
        _ => true,
    };

    /// <summary>
    /// This value as a string when it already is one, otherwise <see langword="null"/>. Does not
    /// coerce; see <see cref="IJsRealm.ToJsString"/> for that.
    /// </summary>
    public string? AsString => _kind == JsValueKind.String ? (string)_reference! : null;

    /// <summary>
    /// A best-effort rendering that never enters the engine, for diagnostics and logging only.
    /// </summary>
    /// <remarks>
    /// An object renders as <c>[object]</c> rather than running its <c>toString</c>, because a
    /// <c>ToString()</c> override that can execute page script, throw, or re-enter the realm is a trap
    /// — and the bridge's 343 <c>ToString()</c> calls were exactly that. Anything that needs the real
    /// ECMAScript conversion must ask the realm.
    /// </remarks>
    public override string ToString() => _kind switch
    {
        JsValueKind.Missing => "<missing>",
        JsValueKind.Undefined => "undefined",
        JsValueKind.Null => "null",
        JsValueKind.Boolean => _number != 0d ? "true" : "false",
        JsValueKind.Number => JsNumberFormat.ToJsString(_number),
        JsValueKind.String => (string)_reference!,
        JsValueKind.Function => "[function]",
        JsValueKind.Array => "[array]",
        _ => "[object]",
    };

    /// <summary>
    /// ECMAScript strict equality (<c>===</c>) as far as it can be decided without entering the
    /// engine — which is all of it, because <c>===</c> never coerces.
    /// </summary>
    /// <remarks>
    /// NaN is not equal to itself here, as the language says. <see cref="Equals(JsValue)"/> deliberately
    /// differs on exactly that one case; see its remarks.
    /// </remarks>
    public static bool operator ==(JsValue left, JsValue right)
    {
        if (left._kind != right._kind)
            return false;

        return left._kind switch
        {
            JsValueKind.Missing or JsValueKind.Undefined or JsValueKind.Null => true,
            JsValueKind.Boolean or JsValueKind.Number => left._number == right._number,
            JsValueKind.String => string.Equals((string)left._reference!, (string)right._reference!, StringComparison.Ordinal),
            _ => ReferenceEquals(left._reference, right._reference),
        };
    }

    /// <summary>The negation of <see cref="op_Equality"/>.</summary>
    public static bool operator !=(JsValue left, JsValue right) => !(left == right);

    /// <summary>
    /// Reflexive equality, for use as a dictionary key or a <c>List.Contains</c> probe.
    /// </summary>
    /// <remarks>
    /// This differs from <c>===</c> on NaN alone: <c>NaN.Equals(NaN)</c> is <see langword="true"/> here
    /// and <c>NaN === NaN</c> is <see langword="false"/>. That is the same split
    /// <see cref="double"/> itself makes, and for the same reason — a value that is not equal to
    /// itself cannot be found in the collection it was put into. The bridge stores JS values in
    /// <c>List&lt;&gt;</c>s (event listeners, collection contents) and the BCL reaches
    /// <see cref="Equals(object)"/> and <see cref="GetHashCode"/> when it searches them, so both have
    /// to be real rather than throw.
    /// </remarks>
    public bool Equals(JsValue other)
    {
        if (_kind != other._kind)
            return false;

        return _kind switch
        {
            JsValueKind.Missing or JsValueKind.Undefined or JsValueKind.Null => true,
            JsValueKind.Boolean or JsValueKind.Number => _number.Equals(other._number),
            JsValueKind.String => string.Equals((string)_reference!, (string)other._reference!, StringComparison.Ordinal),
            _ => ReferenceEquals(_reference, other._reference),
        };
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is JsValue other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => _kind switch
    {
        JsValueKind.Missing or JsValueKind.Undefined or JsValueKind.Null => (int)_kind,
        JsValueKind.Boolean or JsValueKind.Number => HashCode.Combine(_kind, _number),
        JsValueKind.String => HashCode.Combine(_kind, ((string)_reference!).GetHashCode(StringComparison.Ordinal)),
        _ => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_reference),
    };

    /// <summary>Mints a value the engine owns. Providers only — see <see cref="Providers.JsProviderValue"/>.</summary>
    internal static JsValue FromReference(JsValueKind kind, object reference) => new(kind, 0d, reference);
}

/// <summary>
/// Number-to-string in the shape ECMAScript's <c>ToString(Number)</c> produces, for the diagnostic
/// rendering <see cref="JsValue.ToString"/> does without entering the engine.
/// </summary>
/// <remarks>
/// Not a conformant implementation of the specification's algorithm and not used for anything a page
/// can observe — a value that reaches script goes through the engine's own conversion. It exists so
/// that a log line reads <c>1</c> rather than <c>1.0</c>, which the CLR's default would give and which
/// made bridge diagnostics disagree with the page they described.
/// </remarks>
internal static class JsNumberFormat
{
    internal static string ToJsString(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsPositiveInfinity(value))
            return "Infinity";
        if (double.IsNegativeInfinity(value))
            return "-Infinity";

        return value.ToString("R", CultureInfo.InvariantCulture);
    }
}
