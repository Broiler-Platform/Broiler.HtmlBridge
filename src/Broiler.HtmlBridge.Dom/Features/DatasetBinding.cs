using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>Element.dataset</c> — the HTML <c>DOMStringMap</c> view over an element's <c>data-*</c>
/// attributes (HTML §3.2.6.6).
/// <para>
/// It was missing entirely, and reading a property of a missing object is not a quiet failure: it
/// throws, which aborts the whole script. google.com's async-request module — the code that runs
/// <em>when a search is issued</em> — reads <c>b.dataset.ved</c> and writes ids back through the
/// same map, so the map's absence stopped that script dead with
/// <c>Cannot set property eqid of undefined</c>.
/// </para>
/// <para>
/// The map has to be live and open-ended: a page may read a <c>data-*</c> attribute the markup
/// carries, overwrite it, or invent one that no attribute backs yet. A snapshot object built from
/// the attributes present at the time would serve the first two and silently drop the third — the
/// write would land on a throwaway object and never reach the element. So this is a
/// <c>Proxy</c> whose traps read and write the attributes themselves, and the element's attributes
/// remain the single source of truth: nothing is cached, and <c>getAttribute</c> and
/// <c>dataset</c> cannot disagree.
/// </para>
/// </summary>
internal static class DatasetBinding
{
    /// <summary>
    /// The JavaScript half: a factory taking the four accessors as functions and returning the
    /// proxy. It is a compile-time constant of this assembly, evaluated once per context (and
    /// served from the shared code cache during registration like the bridge's other sources).
    /// <para>
    /// <c>getOwnPropertyDescriptor</c> is not optional decoration: <c>Object.keys</c>,
    /// <c>JSON.stringify</c> and the spread operator all filter <c>ownKeys</c> through it, so a
    /// proxy that answers <c>ownKeys</c> alone enumerates as empty.
    /// </para>
    /// </summary>
    private const string FactorySource = @"
(function(get, set, del, keys) {
  return new Proxy({}, {
    get: function(target, name) {
      if (typeof name !== 'string') return undefined;
      var value = get(name);
      return value === null ? undefined : value;
    },
    set: function(target, name, value) {
      if (typeof name === 'string') set(name, String(value));
      return true;
    },
    has: function(target, name) {
      return typeof name === 'string' && get(name) !== null;
    },
    deleteProperty: function(target, name) {
      if (typeof name === 'string') del(name);
      return true;
    },
    ownKeys: function() { return keys(); },
    getOwnPropertyDescriptor: function(target, name) {
      if (typeof name !== 'string') return undefined;
      var value = get(name);
      if (value === null) return undefined;
      return { value: value, writable: true, enumerable: true, configurable: true };
    },
  });
})";

    /// <summary>
    /// Builds the live <c>DOMStringMap</c> for <paramref name="element"/>. Returns <c>null</c> when
    /// the engine has no <c>Proxy</c> to build it from, so the caller can leave <c>dataset</c>
    /// unregistered rather than publish a map that silently drops writes.
    /// </summary>
    // The factory is a per-realm constant, so it is compiled and evaluated once per context rather
    // than once per element: a document has thousands of elements and would otherwise pay an Eval
    // and a fresh closure for each. Keyed weakly so a finished document's context is collectable.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JSContext, JSFunction> Factories = new();

    private static JSFunction? FactoryFor(JSContext context)
    {
        if (Factories.TryGetValue(context, out var cached))
            return cached;

        if (context.Eval(FactorySource, "broiler:dataset") is not JSFunction factory)
            return null;

        Factories.AddOrUpdate(context, factory);
        return factory;
    }

    internal static JSObject? Build(JSContext context, DomElement element, Action<DomElement>? onAttributeChanged)
    {
        if (FactoryFor(context) is not { } factory)
            return null;

        var get = new DomFunction((in a) =>
        {
            var attribute = AttributeNameOf(NameArgument(in a));
            if (attribute == null)
                return JSNull.Value;

            // JSNull rather than undefined so the traps can tell "no such data-* attribute" from an
            // attribute whose value is the empty string, which is a real, readable value.
            return element.GetAttribute(attribute) is { } value ? new JSString(value) : JSNull.Value;
        }, "get", 1);

        var set = new DomFunction((in a) =>
        {
            var attribute = AttributeNameOf(NameArgument(in a));
            if (attribute != null)
            {
                element.SetAttribute(attribute, a.Length > 1 ? a[1].ToString() : string.Empty);
                onAttributeChanged?.Invoke(element);
            }

            return JSUndefined.Value;
        }, "set", 2);

        var del = new DomFunction((in a) =>
        {
            var attribute = AttributeNameOf(NameArgument(in a));
            if (attribute != null && element.RemoveAttribute(attribute))
                onAttributeChanged?.Invoke(element);

            return JSUndefined.Value;
        }, "delete", 1);

        var keys = new DomFunction((in _) =>
        {
            var names = new JSArray();
            foreach (var (key, _) in element.Attributes)
            {
                if (PropertyNameOf(key.LocalName) is { } propertyName)
                    names.Add(new JSString(propertyName));
            }

            return names;
        }, "keys", 0);

        return factory.InvokeFunction(new Arguments(JSUndefined.Value, get, set, del, keys)) as JSObject;
    }

    private static string? NameArgument(in Arguments a) => a.Length > 0 ? a[0].ToString() : null;

    /// <summary>
    /// The <c>data-*</c> attribute a <c>dataset</c> property name addresses: each ASCII uppercase
    /// letter becomes <c>-</c> plus its lowercase form, and the whole is prefixed with <c>data-</c>
    /// (so <c>dataset.fooBar</c> is <c>data-foo-bar</c>).
    /// <para>
    /// A name that already contains <c>-</c> followed by an ASCII lowercase letter has no attribute:
    /// the mapping back from attributes never produces one, so honouring it would let
    /// <c>dataset.fooBar</c> and <c>dataset['foo-bar']</c> address the same attribute under two
    /// spellings. The spec makes it a <c>SyntaxError</c>; this reports "no such property", which is
    /// what the traps need and what keeps a page's stray read from aborting its script.
    /// </para>
    /// </summary>
    internal static string? AttributeNameOf(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
            return null;

        var builder = new System.Text.StringBuilder("data-", propertyName.Length + 6);
        for (var i = 0; i < propertyName.Length; i++)
        {
            var c = propertyName[i];
            if (c == '-' && i + 1 < propertyName.Length && propertyName[i + 1] is >= 'a' and <= 'z')
                return null;

            if (c is >= 'A' and <= 'Z')
            {
                builder.Append('-').Append((char)(c + ('a' - 'A')));
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The <c>dataset</c> property name a <c>data-*</c> attribute is seen under — the inverse of
    /// <see cref="AttributeNameOf"/>. Returns <c>null</c> for an attribute that is not
    /// <c>data-</c>-prefixed, which is how enumeration skips the element's other attributes.
    /// </summary>
    internal static string? PropertyNameOf(string attributeName)
    {
        const string Prefix = "data-";
        if (!attributeName.StartsWith(Prefix, StringComparison.Ordinal) || attributeName.Length == Prefix.Length)
            return null;

        var builder = new System.Text.StringBuilder(attributeName.Length - Prefix.Length);
        for (var i = Prefix.Length; i < attributeName.Length; i++)
        {
            var c = attributeName[i];
            if (c == '-' && i + 1 < attributeName.Length && attributeName[i + 1] is >= 'a' and <= 'z')
            {
                builder.Append((char)(attributeName[++i] - ('a' - 'A')));
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
