using Broiler.JavaScript.Ast.Misc;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// A native DOM callable — a WebIDL operation (<c>appendChild</c>, <c>setAttribute</c>, …) or an
/// attribute accessor (<c>get id</c>, <c>set textContent</c>, …) — installed on a node's JS wrapper.
/// </summary>
/// <remarks>
/// <para>
/// Identical to <see cref="JSFunction"/> except that it is built with <c>createPrototype: false</c>,
/// so it carries no <c>prototype</c> object. That is what WebIDL requires: only interface objects
/// are constructors, while operations and attribute accessors are ordinary non-constructor
/// functions — <c>el.setAttribute.prototype</c> is <c>undefined</c> and <c>new el.setAttribute()</c>
/// throws a TypeError. The engine's own builtins already register their members this way
/// (<c>BuiltInsAssemblyInitializer</c> passes <c>createPrototype: false</c> throughout); the DOM
/// bridge did not, so every wrapper member also minted a throwaway prototype object.
/// </para>
/// <para>
/// That mattered because a node's wrapper is built eagerly and per node: an element gets ~149 own
/// members, each of which allocated a <see cref="JSFunction"/> <em>plus</em> a second
/// <see cref="JSObject"/> for its unreachable <c>prototype</c> plus that object's
/// <c>constructor</c> back-reference. Dropping the prototype roughly halves the retained cost of a
/// wrapper — the difference between a document of 10k script-created elements fitting in the WPT
/// per-test memory budget and being aborted (<c>css/css-variables/url-syntax-crash.html</c>).
/// </para>
/// <para>
/// Use this for anything installed on a node, declaration, or collection wrapper. Interface objects
/// that JS may legitimately <c>new</c> (<c>URL</c>, <c>Response</c>, <c>Headers</c>, … — registered
/// in <c>DomBridge/Registration/</c> and <c>Features/FetchBinding.cs</c>) must keep using
/// <see cref="JSFunction"/>, because <c>createPrototype: false</c> also makes a function
/// non-constructable (<c>JSConstructorOperations.IsConstructor</c> tests
/// <c>prototype != null || IsConstructable</c>).
/// </para>
/// </remarks>
internal sealed class DomFunction : JSFunction
{
    internal DomFunction(JSFunctionDelegate f, in StringSpan name, int length = 0)
        : base(f, in name, StringSpan.Empty, length, createPrototype: false)
    {
    }
}
