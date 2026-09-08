namespace Broiler.HtmlBridge.Jseal;

// The realm surface is split into six narrow capability contracts, the way IScriptEngine was split in
// this repository's Phase 8. IJsRealm (see IJsRealm.cs) aggregates them, and every binding depends on
// the aggregate, so nothing at a call site gets longer. The split exists for the other two readers:
// a provider, which implements them one at a time and can say in its own source which group a file
// serves; and a reviewer asking what an engine must be able to do, who gets six answerable questions
// instead of one surface of forty members.

/// <summary>
/// Creating values, and the conversions between a JavaScript value and a CLR one that only the engine
/// can perform.
/// </summary>
/// <remarks>
/// The cheap conversions are not here — they are on <see cref="JsValue"/> itself
/// (<see cref="JsValue.AsBoolean"/>, <see cref="JsValue.AsNumber"/>, <see cref="JsValue.AsString"/>),
/// because they are decidable from the handle and a host that has to enter the engine to ask whether
/// a value is truthy will do it on every branch of every callback. What is here is the set that can
/// run user code: <c>ToString</c> on an object may call a <c>toString</c> the page wrote.
/// </remarks>
public interface IJsValues
{
    /// <summary>A new ordinary object with the realm's <c>Object.prototype</c>.</summary>
    JsValue NewObject();

    /// <summary>A new Array, optionally pre-filled.</summary>
    JsValue NewArray(ReadOnlySpan<JsValue> elements = default);

    /// <summary>
    /// A new non-constructable host function — a WebIDL operation or attribute accessor.
    /// </summary>
    /// <remarks>
    /// <b>Non-constructable is the default because WebIDL says so, and because it is what makes a
    /// wrapper affordable.</b> Only interface objects are constructors; <c>el.setAttribute.prototype</c>
    /// is <c>undefined</c> and <c>new el.setAttribute()</c> throws. Under Broiler.JS this maps to
    /// <c>createPrototype: false</c>, which the bridge adopted as a memory fix as much as a
    /// correctness one — an element wrapper's members were each allocating an unreachable prototype
    /// object plus its <c>constructor</c> back-reference. Anything a page may legitimately
    /// <c>new</c> asks for <see cref="NewConstructor"/> instead, and there are sixteen of those.
    /// </remarks>
    /// <param name="name">The function's <c>name</c>.</param>
    /// <param name="body">The host code to run.</param>
    /// <param name="length">The function's declared <c>length</c> — its count of required arguments.</param>
    JsValue NewMethod(string name, JsNativeFunction body, int length = 0);

    /// <summary>
    /// A new constructable host function — an interface object a page may <c>new</c>
    /// (<c>Headers</c>, <c>Request</c>, <c>Response</c>, <c>FormData</c>, <c>Worker</c>, …).
    /// </summary>
    /// <remarks>
    /// The returned function carries a <c>prototype</c> object, reachable with
    /// <see cref="IJsMembers.GetProperty"/>, which is where an interface's members are installed.
    /// </remarks>
    JsValue NewConstructor(string name, JsNativeFunction body, int length = 0);

    /// <summary>
    /// A new object whose property lookup the host completes — a live collection, a style
    /// declaration, a storage area. Requires <see cref="JsCapabilities.ExoticObjects"/>.
    /// </summary>
    /// <remarks>
    /// The handler answers only what the object's ordinary properties did not; see
    /// <see cref="IJsExotic"/> for why that order is not negotiable.
    /// </remarks>
    JsValue NewExotic(IJsExotic handler);

    /// <summary>
    /// ECMAScript <c>ToString</c>. Enters the engine, and may run page script or throw.
    /// </summary>
    string ToJsString(JsValue value);

    /// <summary>
    /// ECMAScript <c>ToNumber</c>. Enters the engine, and may run page script or throw.
    /// </summary>
    double ToNumber(JsValue value);
}

/// <summary>
/// Installing, reading and removing an object's members, and its prototype link.
/// </summary>
public interface IJsMembers
{
    /// <summary>Installs a data property.</summary>
    void DefineValue(JsValue target, string name, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default);

    /// <summary>
    /// Installs an accessor property. A <see langword="null"/> <paramref name="setter"/> makes it
    /// read-only, which is how the bridge expresses a read-only IDL attribute at 216 sites.
    /// </summary>
    void DefineAccessor(JsValue target, string name, JsNativeFunction getter, JsNativeFunction? setter, JsPropertyFlags flags = JsPropertyFlags.Default);

    /// <summary>Installs an integer-indexed data property.</summary>
    void DefineIndex(JsValue target, uint index, JsValue value, JsPropertyFlags flags = JsPropertyFlags.Default);

    /// <summary>Reads a property, following the prototype chain. May run a getter the page wrote.</summary>
    JsValue GetProperty(JsValue target, string name);

    /// <summary>Reads an integer-indexed property, following the prototype chain.</summary>
    JsValue GetIndex(JsValue target, uint index);

    /// <summary>Writes a property. May run a setter the page wrote.</summary>
    void SetProperty(JsValue target, string name, JsValue value);

    /// <summary>Whether the property exists, own or inherited.</summary>
    bool HasProperty(JsValue target, string name);

    /// <summary>Deletes an own property, answering whether it is now absent.</summary>
    bool DeleteProperty(JsValue target, string name);

    /// <summary>The object's own enumerable string-keyed property names, in property-creation order.</summary>
    IReadOnlyList<string> OwnPropertyNames(JsValue target);

    /// <summary>
    /// Points <paramref name="target"/>'s prototype chain at <paramref name="prototype"/> — how a DOM
    /// wrapper is linked to its interface so that <c>Object.getPrototypeOf(el) === Element.prototype</c>
    /// and <c>el.constructor.name</c> answer the interface rather than <c>Object</c>.
    /// </summary>
    void SetPrototype(JsValue target, JsValue prototype);

    /// <summary>The object's prototype, or <see cref="JsValue.Null"/>.</summary>
    JsValue GetPrototype(JsValue target);
}

/// <summary>
/// Calling into JavaScript, and raising a JavaScript error from host code.
/// </summary>
public interface IJsCalls
{
    /// <summary>Calls a function.</summary>
    JsValue Invoke(JsValue function, JsValue thisValue, ReadOnlySpan<JsValue> arguments = default);

    /// <summary>Calls a constructor with <c>new</c>.</summary>
    JsValue Construct(JsValue constructor, ReadOnlySpan<JsValue> arguments = default);

    /// <summary>
    /// The exception to <see langword="throw"/> so that JavaScript sees an error of
    /// <paramref name="kind"/> with <paramref name="message"/>.
    /// </summary>
    /// <remarks>
    /// It returns rather than throws so that a callback body reads <c>throw realm.Error(…)</c>, which
    /// tells the compiler the path ends and the reader that the throw is deliberate — a helper that
    /// threw would leave the compiler thinking control continued.
    /// </remarks>
    Exception Error(JsErrorKind kind, string message);

    /// <summary>
    /// The DOM exception to <see langword="throw"/> so that JavaScript sees a <c>DOMException</c> with
    /// the given <c>name</c> — <c>NotFoundError</c>, <c>HierarchyRequestError</c>, and the rest.
    /// </summary>
    Exception DomError(string name, string message);
}

/// <summary>
/// The realm's job queue: the promise reactions and <c>queueMicrotask</c> callbacks that run between
/// one piece of script and the next.
/// </summary>
/// <remarks>
/// <b>Pull, not push.</b> The host drives this — it decides when a microtask checkpoint happens,
/// because in a browser that decision belongs to the event loop and not to the engine. Broiler.JS
/// pushes instead, through a <c>SynchronizationContext</c> captured when the realm is built, and the
/// provider is what turns that into the pull shape here. An engine with an explicit
/// "drain the job queue" entry point implements this directly.
/// </remarks>
public interface IJsJobs
{
    /// <summary>Queues a host callback as a microtask.</summary>
    void EnqueueJob(Action job);

    /// <summary>
    /// Runs queued jobs until the queue is empty or <paramref name="limit"/> have run, answering how
    /// many ran. A job that queues another is followed, which is why there is a limit at all.
    /// </summary>
    int DrainJobs(int limit = 10_000);

    /// <summary>Whether any job is queued.</summary>
    bool HasPendingJobs { get; }

    /// <summary>
    /// A new pending promise, with the two functions that settle it.
    /// </summary>
    /// <remarks>
    /// Handing back <paramref name="resolve"/> and <paramref name="reject"/> rather than taking an
    /// executor is the shape the bridge actually needs: its one deferred promise
    /// (<c>customElements.whenDefined</c>) captures the resolve function out of the executor and
    /// stores it, which only works because Broiler.JS happens to run the executor synchronously.
    /// Depending on that is depending on an engine's scheduling; returning the pair does not.
    /// </remarks>
    JsValue NewPromise(out Action<JsValue> resolve, out Action<JsValue> reject);
}

/// <summary>
/// Turning JavaScript source into something that runs — and the distinction between the two very
/// different reasons a browser does that.
/// </summary>
/// <remarks>
/// <para>
/// <b>Host script and guest source are not the same capability, and conflating them is what makes a
/// second engine look impossible.</b> The bridge itself authors JavaScript: two embedded <c>.js</c>
/// assets totalling 1,891 lines, plus 54 <c>Eval</c> sites across 28 files that install polyfills,
/// probe for a global, or re-link a prototype. That source is written by this repository, ships with
/// it, and is not subject to the page's Content-Security-Policy. Guest source is what <c>eval</c>,
/// <c>new Function</c> and a dynamic <c>import()</c> ask for on the page's behalf, and it is exactly
/// what a CSP may forbid.
/// </para>
/// <para>
/// An engine with no run-time compiler can support the first by compiling the bridge's own JavaScript
/// when the engine is built, and refuse the second — which is the shape Broiler.VM already has, where
/// refusing is expressed by registering no artifact provider at all rather than by consulting a policy
/// object mid-execution. Declaring them separately is what lets a provider say so.
/// </para>
/// </remarks>
public interface IJsSource
{
    /// <summary>
    /// Runs JavaScript this repository authored. Not subject to the page's content policy.
    /// </summary>
    /// <param name="source">The script text.</param>
    /// <param name="label">
    /// A name for the evaluation, used as the location in a stack frame — <c>polyfill:streams</c>,
    /// <c>probe:module-support</c>. Diagnostics only.
    /// </param>
    JsValue EvaluateHostScript(string source, string label);

    /// <summary>
    /// Runs JavaScript the page supplied, on the page's behalf.
    /// </summary>
    /// <remarks>
    /// Throws when the realm was not built with <see cref="JsCapabilities.GuestEval"/> — which is what
    /// a page whose policy forbids evaluation gets, and is a contract outcome the page may catch
    /// rather than a check the engine performs.
    /// </remarks>
    JsValue EvaluateGuestSource(string source, string label);
}
