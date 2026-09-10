using System.Diagnostics.CodeAnalysis;

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
    /// <remarks>
    /// <para>
    /// <b>Built out of the realm's own <c>ArrayBuffer</c>, because the profile's host surface has no
    /// binary member</b> - the same route <c>NewPromise</c> takes to the realm's own <c>Promise</c>.
    /// The buffer that comes back is the realm's: a page can <c>new Uint8Array(b)</c> it,
    /// <c>b.slice()</c> it and find it <c>instanceof ArrayBuffer</c>.
    /// </para>
    /// <para>
    /// <b>The bytes go across in chunks, and the chunking is not a performance nicety.</b> Every
    /// crossing of this host surface charges one unit against a host-call allowance that defaults to
    /// a million, and spending it raises a termination rather than anything a page could catch. A
    /// crossing per byte would therefore abort the program on a blob of about a megabyte - not run
    /// slowly, abort - so the bytes are handed over a chunk at a time through
    /// <c>%TypedArray%.prototype.set</c>: two crossings per chunk plus two, rather than one per byte.
    /// </para>
    /// <para>
    /// The ceiling that remains is the guest's memory rather than its crossings. A buffer of n bytes
    /// costs n bytes in the realm plus a chunk-sized temporary, against a default allocation budget
    /// of sixty-four megabytes, and a request past that fails as a resource exhaustion naming the
    /// dimension.
    /// </para>
    /// </remarks>
    public JsValue NewArrayBuffer(ReadOnlySpan<byte> bytes)
    {
        if ((Capabilities & JsCapabilities.BinaryData) == 0)
            throw Lacking(JsCapabilities.BinaryData);

        // Copied out of the span before the crossing, because the span cannot outlive this frame and
        // the lambda below runs inside a step.
        var source = bytes.ToArray();

        return VmMarshal.Wrap(InStep(realm =>
        {
            var buffer = realm.Construct(_bridge.ArrayBuffer, [JsHostValue.Number(source.Length)]);
            var view = realm.Construct(_bridge.Uint8Array, [buffer]);

            for (var offset = 0; offset < source.Length; offset += TransferChunk)
            {
                var length = Math.Min(TransferChunk, source.Length - offset);
                var chunk = new JsHostValue[length];

                for (var i = 0; i < length; i++)
                    chunk[i] = JsHostValue.Number(source[offset + i]);

                realm.Invoke(
                    _bridge.TypedArraySet,
                    view,
                    [realm.NewArray(chunk), JsHostValue.Number(offset)]);
            }

            return buffer;
        }));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// <b>The brand check is <c>ArrayBuffer.prototype</c>'s own <c>byteLength</c> getter, invoked
    /// with the candidate as <c>this</c>.</b> Its body asks whether the receiver is the engine's
    /// buffer type and throws a TypeError otherwise, so one crossing answers both halves of this
    /// member - and answers by CLR type rather than by anything a page can write. A
    /// <c>DataView</c> and every typed array answer <c>byteLength</c> themselves, an object's
    /// prototype is settable and its <c>Symbol.toStringTag</c> is writable, so none of the three
    /// JS-visible routes would be an answer.
    /// </para>
    /// <para>
    /// <b>There is no <c>SharedArrayBuffer</c> to exclude here</b>, which the profile states as a
    /// deliberate omission rather than an unfinished one. The other provider's engine has one and
    /// declares it a subclass of the ordinary buffer, so that provider excludes it by hand; this one
    /// has nothing to exclude, and saying so is what stops a later reader adding a check that could
    /// never fire.
    /// </para>
    /// <para>
    /// <b>The bytes come back through the one wide channel the host surface has, which is a
    /// string.</b> Nothing here charges per character, so a chunk of the buffer converted to one
    /// character per byte crosses in a single call; the alternative, a crossing per byte, would spend
    /// the host-call allowance and abort. The conversion is
    /// <c>String.fromCharCode.apply(null, view.subarray(...))</c> with every one of those four
    /// functions captured before any page script ran, so a page that replaced <c>String</c>,
    /// <c>Uint8Array</c> or either prototype member cannot reach it.
    /// </para>
    /// </remarks>
    public bool TryGetArrayBufferBytes(JsValue value, [NotNullWhen(true)] out byte[]? bytes)
    {
        if ((Capabilities & JsCapabilities.BinaryData) == 0)
            throw Lacking(JsCapabilities.BinaryData);

        var candidate = VmMarshal.Unwrap(value);

        if (candidate.Kind is not JsHostValueKind.Object)
        {
            bytes = null;
            return false;
        }

        bytes = InStep(realm =>
        {
            int length;

            try
            {
                // Caught inside the step, before Translate turns a guest throw into a
                // JsEngineException: the throw IS the answer here rather than a failure.
                length = (int)realm.Invoke(_bridge.ArrayBufferByteLength, candidate, []).AsNumber();
            }
            catch (JsHostThrowException)
            {
                return null;
            }

            if (length <= 0)
                return [];

            var read = new byte[length];
            var view = realm.Construct(_bridge.Uint8Array, [candidate]);

            for (var offset = 0; offset < length; offset += TransferChunk)
            {
                var end = Math.Min(offset + TransferChunk, length);

                var part = realm.Invoke(
                    _bridge.TypedArraySubarray,
                    view,
                    [JsHostValue.Number(offset), JsHostValue.Number(end)]);

                var text = realm.Invoke(
                    _bridge.FunctionApply,
                    _bridge.StringFromCharCode,
                    [JsHostValue.Undefined, part]).AsString();

                for (var i = 0; i < text.Length; i++)
                    read[offset + i] = (byte)text[i];
            }

            return read;
        });

        return bytes is not null;
    }

    /// <summary>
    /// How many bytes cross at a time, in both directions.
    /// </summary>
    /// <remarks>
    /// <b>Bounded above by the argument count <c>Function.prototype.apply</c> will spread</b> - the
    /// read path applies <c>String.fromCharCode</c> to a chunk, so a chunk is an argument list - and
    /// bounded below by the host-call allowance, since every chunk costs two crossings in each
    /// direction. Eight thousand is comfortably inside every engine's spread limit and puts a
    /// thirty-two megabyte buffer at about eight thousand crossings against an allowance of a
    /// million.
    /// </remarks>
    private const int TransferChunk = 8000;

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
