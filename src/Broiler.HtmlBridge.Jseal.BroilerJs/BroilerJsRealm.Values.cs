using Broiler.JavaScript.Ast.Misc;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Jseal.BroilerJs;

/// <summary>
/// <see cref="IJsValues"/>: minting the realm's own values, and the two coercions that can run page
/// script.
/// </summary>
internal sealed partial class BroilerJsRealm
{
    /// <inheritdoc />
    public JsValue NewObject()
    {
        using var scope = Enter();

        // The realm scope is doing real work on this line: JSObject's parameterless constructor takes
        // its prototype from the current context, so the same expression outside the scope produces
        // an object with no Object.prototype.
        return BroilerJsMarshal.Wrap(new JSObject());
    }

    /// <inheritdoc />
    public JsValue NewArray(ReadOnlySpan<JsValue> elements = default)
    {
        using var scope = Enter();

        if (elements.IsEmpty)
            return BroilerJsMarshal.Wrap(new JSArray());

        var items = new JSValue[elements.Length];
        for (var i = 0; i < elements.Length; i++)
            items[i] = BroilerJsMarshal.Unwrap(elements[i]);

        return BroilerJsMarshal.Wrap(new JSArray(items));
    }

    /// <summary>
    /// A non-constructable host function.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>createPrototype: false</c> is not an optimisation dial; it is what makes the function
    /// non-constructable, which is what WebIDL requires of an operation or an attribute accessor.</b>
    /// The engine's <c>JSConstructorOperations.IsConstructor</c> tests
    /// <c>prototype != null || IsConstructable</c>, so a function built with a prototype object can be
    /// <c>new</c>-ed — and <c>el.setAttribute.prototype</c> would answer an object where a browser
    /// answers <c>undefined</c>.
    /// </para>
    /// <para>
    /// It is also the memory fix <c>DomBridge/DomFunction.cs</c> records, and the reason that file
    /// exists. A node's wrapper is built eagerly and per node with roughly 149 own members, each of
    /// which was allocating a <c>JSFunction</c> <em>plus</em> an unreachable prototype object plus
    /// that object's <c>constructor</c> back-reference. Dropping the prototype roughly halves the
    /// retained cost of a wrapper — the difference between a document of 10k script-created elements
    /// fitting in the WPT per-test memory budget and being aborted. Every member the bridge installs
    /// comes through here, so the same saving now belongs to the provider rather than to a bridge
    /// type the bridge had to remember to use.
    /// </para>
    /// </remarks>
    public JsValue NewMethod(string name, JsNativeFunction body, int length = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var scope = Enter();

        var function = new JSFunction(
            MethodTrampoline(body), name ?? string.Empty, StringSpan.Empty, length, createPrototype: false);

        return BroilerJsMarshal.Wrap(function);
    }

    /// <summary>
    /// A constructable host function — an interface object a page may <c>new</c>.
    /// </summary>
    /// <remarks>
    /// The same constructor with <c>createPrototype: true</c>, which mints the <c>prototype</c> object
    /// an interface's members are installed on and makes the function pass the engine's constructor
    /// test. There are sixteen of these in the bridge against 1,246 members, which is why this is the
    /// method that has to be asked for by name and <see cref="NewMethod"/> is the default.
    /// </remarks>
    public JsValue NewConstructor(string name, JsNativeFunction body, int length = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        using var scope = Enter();

        var function = new JSFunction(
            ConstructorTrampoline(body), name ?? string.Empty, StringSpan.Empty, length, createPrototype: true);

        return BroilerJsMarshal.Wrap(function);
    }

    /// <inheritdoc />
    public JsValue NewExotic(IJsExotic handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        using var scope = Enter();

        return BroilerJsMarshal.Wrap(new BroilerJsExoticObject(this, handler));
    }

    /// <summary>
    /// ECMAScript <c>ToString</c>.
    /// </summary>
    /// <remarks>
    /// Broiler.JS spells this <c>JSValue.ToString()</c>, and on a <c>JSObject</c> that override runs
    /// the object's own <c>toString</c> — page script, which may throw or re-enter the realm. That is
    /// exactly why <see cref="JsValue.ToString"/> on the handle refuses to do it and answers
    /// <c>[object]</c> instead: the cheap rendering and the observable coercion are different
    /// questions, and only this one is entitled to run code.
    /// </remarks>
    public string ToJsString(JsValue value)
    {
        using var scope = Enter();
        return BroilerJsMarshal.Unwrap(value).ToString();
    }

    /// <summary>
    /// ECMAScript <c>ToNumber</c>.
    /// </summary>
    /// <remarks>
    /// <c>DoubleValue</c> is the engine's coercion, not a field read: on an object it runs
    /// <c>ToPrimitive</c> with a number hint, which may reach a <c>valueOf</c> the page wrote, and on
    /// a Symbol it throws — as the specification says it must.
    /// </remarks>
    public double ToNumber(JsValue value)
    {
        using var scope = Enter();
        return BroilerJsMarshal.Unwrap(value).DoubleValue;
    }
}
