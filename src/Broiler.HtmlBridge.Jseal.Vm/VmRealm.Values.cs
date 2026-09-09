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

        return InStep(realm => VmMarshal.Wrap(realm.NewExotic(new VmExoticObject(handler))));
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
