using Broiler.VM.Profile.JavaScript;

namespace Broiler.HtmlBridge.Jseal.Vm;

/// <summary>
/// <see cref="IJsValues"/>: minting values, and the two coercions that can run guest code.
/// </summary>
internal sealed partial class VmRealm
{
    /// <inheritdoc />
    public JsValue NewObject() => InStep(realm => VmMarshal.Wrap(realm.NewObject()));

    /// <inheritdoc />
    public JsValue NewArray(ReadOnlySpan<JsValue> elements = default)
    {
        var converted = VmMarshal.UnwrapAll(elements);

        return InStep(realm => VmMarshal.Wrap(realm.NewArray(converted)));
    }

    /// <inheritdoc />
    public JsValue NewMethod(string name, JsNativeFunction body, int length = 0)
    {
        ArgumentNullException.ThrowIfNull(body);

        return InStep(realm => VmMarshal.Wrap(realm.NewMethod(name, Trampoline(body), length)));
    }

    /// <inheritdoc />
    public JsValue NewConstructor(string name, JsNativeFunction body, int length = 0)
    {
        ArgumentNullException.ThrowIfNull(body);

        return InStep(realm => VmMarshal.Wrap(realm.NewConstructor(name, Trampoline(body), length)));
    }

    /// <inheritdoc />
    public JsValue NewExotic(IJsExotic handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        if ((Capabilities & JsCapabilities.ExoticObjects) == 0)
            throw Lacking(JsCapabilities.ExoticObjects);

        return InStep(realm =>
        {
            var exotic = realm.NewExotic(new VmExoticObject(handler));

            return VmMarshal.Wrap(
                handler is IJsExoticDelete deleter ? Deleting(realm, exotic, deleter) : exotic);
        });
    }

    /// <summary>
    /// The same host exotic behind a proxy whose one trap routes a named deletion to the handler.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The profile's host-object surface has no delete hook, and this is the route around that
    /// rather than a change to the engine.</b> It is the lesson <c>NewPromise</c> recorded applied
    /// to a second intrinsic: a thing the guest already has is reachable across the host surface
    /// without a new member on it. Three ordinary crossings - the constructor read at realm
    /// creation, a trap minted here, one construct - and no evaluation.
    /// </para>
    /// <para>
    /// <b>ONE trap, and only for a handler that declares a deletion.</b> Every operation on a proxy
    /// costs a property read on the trap object plus a charge before it forwards, which is exactly
    /// the price the profile weighed when it made its host exotic a subclass instead of a proxy. So
    /// the two storage areas pay it and the live collections - whose indexed reads are the hottest
    /// path a page has - are minted exactly as they were. This is the whole reason
    /// <see cref="IJsExoticDelete"/> is a second interface: a provider that could not ask the
    /// question at mint time would have to wrap everything.
    /// </para>
    /// <para>
    /// <b>The trap is the only one defined, so every other operation forwards to the target
    /// unchanged</b> - reads, writes, enumeration, descriptors and the prototype all reach the host
    /// exotic through the proxy's own missing-trap paths, which forward the internal method rather
    /// than an unchecked one. That includes the bridge's own member installation, which reaches the
    /// target while the realm is installing and is correctly not offered to the handler.
    /// </para>
    /// </remarks>
    private JsHostValue Deleting(JsHostRealm realm, JsHostValue exotic, IJsExoticDelete deleter)
    {
        var constructor = _bridge.Proxy;
        var forward = _bridge.ReflectDelete;

        if (constructor.Kind is not JsHostValueKind.Function ||
            forward.Kind is not JsHostValueKind.Function)
        {
            throw new JsEngineException(
                "the realm had no Proxy constructor or no Reflect.deleteProperty when it was created, "
                    + "so this provider cannot complete a deletion on an exotic object");
        }

        var traps = realm.NewObject();

        realm.DefineValue(
            traps,
            "deleteProperty",
            realm.NewMethod(
                "deleteProperty",
                (asked, _, arguments) =>
                {
                    var target = arguments.Length > 0 ? arguments[0] : JsHostValue.Undefined;
                    var key = arguments.Length > 1 ? arguments[1] : JsHostValue.Undefined;

                    // A NAME, NEVER AN INDEX AND NEVER A SYMBOL. This trap is handed every key kind
                    // the guest can delete by, and the profile's own host object routes an
                    // array-index key to the indexed hook and offers none of them to TrySetNamed.
                    // A deletion that reached the named hook with "7" would make this provider
                    // disagree with the other one about what a name is, which is worse than the gap
                    // they share.
                    if (key.Kind is JsHostValueKind.String && !IsArrayIndex(key.AsString()))
                        deleter.TryDeleteNamed(key.AsString());

                    // The ordinary deletion runs either way and its answer is the deletion's answer,
                    // which is also what keeps the proxy's own invariant satisfied. It forwards
                    // through the captured intrinsic rather than through JsHostRealm.DeleteProperty
                    // because that member takes a string name only, so a symbol-keyed deletion would
                    // be dropped in silence - see VmHostBridge.ReflectDelete.
                    return JsHostValue.Boolean(
                        asked.Invoke(forward, JsHostValue.Undefined, [target, key]).AsBoolean());
                },
                length: 2));

        return realm.Construct(constructor, [exotic, traps]);
    }

    /// <summary>
    /// Whether a key is a canonical array index, which is what makes it not a name.
    /// </summary>
    /// <remarks>
    /// Spelled out rather than taken from <c>uint.TryParse</c> so that the leading-zero and
    /// upper-bound rules are visible: <c>"007"</c> and <c>"4294967295"</c> are names, <c>"7"</c> is
    /// an index, and a parse that accepted either would silently widen what reaches the handler.
    /// </remarks>
    private static bool IsArrayIndex(string key)
    {
        if (key.Length is 0 or > 10)
            return false;

        if (key.Length > 1 && key[0] == '0')
            return false;

        ulong value = 0;

        foreach (var digit in key)
        {
            if (digit is < '0' or > '9')
                return false;

            value = (value * 10) + (ulong)(digit - '0');
        }

        return value < uint.MaxValue;
    }

    /// <inheritdoc />
    public string ToJsString(JsValue value)
    {
        var converted = VmMarshal.Unwrap(value);

        return InStep(realm => realm.ToJsString(converted));
    }

    /// <inheritdoc />
    public double ToNumber(JsValue value)
    {
        var converted = VmMarshal.Unwrap(value);

        return InStep(realm => realm.ToNumber(converted));
    }

    /// <summary>
    /// Wraps a JSEAL host body in the shape the VM realm calls.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The call frame is built on this frame's own stack and nothing is allocated per call</b>
    /// beyond the handle conversion the two value models make unavoidable. <see cref="JsCall"/> is a
    /// <see langword="ref"/> struct over a span, which is what lets the arguments live here.
    /// </para>
    /// <para>
    /// <b>Nothing is caught.</b> A body that throws is a body whose exception has to reach the
    /// engine and become something the page can catch, and the VM's own trampoline does that
    /// translation - so catching here would be intercepting a throw on its way to the only code that
    /// knows what to do with it.
    /// </para>
    /// </remarks>
    private JsHostFunction Trampoline(JsNativeFunction body) =>
        (realm, thisValue, arguments) =>
        {
            var converted = arguments.Length == 0
                ? []
                : new JsValue[arguments.Length];

            for (var at = 0; at < arguments.Length; at++)
                converted[at] = VmMarshal.Wrap(arguments[at]);

            var call = new JsCall(
                this,
                VmMarshal.Wrap(thisValue),
                converted,
                VmMarshal.Wrap(realm.NewTarget));

            try
            {
                return VmMarshal.Unwrap(body(in call));
            }
            catch (JsEngineException raised) when (!raised.Thrown.IsMissing)
            {
                // A HOST BODY THAT THREW A GUEST VALUE IS THROWING, NOT FAILING. `realm.Error` and
                // `realm.DomError` answer with one of these so a body can write `throw
                // realm.Error(...)`, and it has to arrive in the engine as the guest throw it
                // carries rather than as a CLR exception unwinding through interpreter frames,
                // which is a state no `catch` in the page could reason about.
                //
                // THE ORIGINAL IS RETHROWN RATHER THAN REBUILT. The engine's own throw type is the
                // engine's to construct, and rebuilding one from the outside would mean this
                // assembly deciding what a guest throw is - so the wrapper is unwrapped and the
                // exception the engine made goes back the way it came.
                throw realm.Throw(VmMarshal.Unwrap(raised.Thrown));
            }
        };
}
