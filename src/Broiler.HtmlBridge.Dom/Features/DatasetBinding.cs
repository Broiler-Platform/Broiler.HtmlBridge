using System.Runtime.CompilerServices;

using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
    /// proxy. It is a compile-time constant of this assembly, evaluated once per realm (and
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

    // The factory is a per-realm constant, so it is compiled and evaluated once per realm rather
    // than once per element: a document has thousands of elements and would otherwise pay an
    // evaluation and a fresh closure for each. Keyed weakly so a finished document's realm — and the
    // engine values it owns — stay collectable. The handle is boxed because a weak table's value has
    // to be a reference type and JsValue is a struct.
    private static readonly ConditionalWeakTable<IJsRealm, StrongBox<JsValue>> Factories = new();

    /// <summary>
    /// The realm's one proxy factory, or a non-function value when the realm has no <c>Proxy</c>.
    /// </summary>
    /// <remarks>
    /// The source is this repository's own, not the page's, so it goes through
    /// <see cref="IJsSource.EvaluateHostScript"/> — the half of the source contract that is exempt
    /// from the page's content policy. A failure is not cached, exactly as before: a realm that
    /// answered nothing once is asked again rather than remembered as broken.
    /// </remarks>
    private static JsValue FactoryFor(IJsRealm realm)
    {
        if (Factories.TryGetValue(realm, out var cached))
            return cached.Value;

        var factory = realm.EvaluateHostScript(FactorySource, "broiler:dataset");
        if (!factory.IsFunction)
            return JsValue.Undefined;

        Factories.AddOrUpdate(realm, new StrongBox<JsValue>(factory));
        return factory;
    }

    /// <summary>
    /// Builds the live <c>DOMStringMap</c> for <paramref name="element"/>. Answers a non-object when
    /// the realm has no <c>Proxy</c> to build it from, so the caller can leave <c>dataset</c>
    /// unregistered rather than publish a map that silently drops writes.
    /// </summary>
    /// <param name="realm">The realm the factory, the four traps' callbacks and the proxy belong to.</param>
    /// <param name="element">The element whose <c>data-*</c> attributes the map is a view over.</param>
    /// <param name="onAttributeChanged">Run after a write or a delete reaches the element.</param>
    internal static JsValue Build(IJsRealm realm, DomElement element, Action<DomElement>? onAttributeChanged)
    {
        var factory = FactoryFor(realm);
        if (!factory.IsFunction)
            return JsValue.Undefined;

        var get = realm.NewMethod("get", (in call) =>
        {
            var attribute = AttributeNameOf(NameArgument(in call));
            if (attribute == null)
                return JsValue.Null;

            // Null rather than undefined so the traps can tell "no such data-* attribute" from an
            // attribute whose value is the empty string, which is a real, readable value.
            return element.GetAttribute(attribute) is { } value ? JsValue.String(value) : JsValue.Null;
        }, 1);

        var set = realm.NewMethod("set", (in call) =>
        {
            var attribute = AttributeNameOf(NameArgument(in call));
            if (attribute != null)
            {
                // The realm's ToString, not the handle's: the trap already passed String(value), but
                // this operation is reachable by name and the coercion a caller observes is the
                // ECMAScript one.
                element.SetAttribute(attribute, call.Length > 1 ? call.Realm.ToJsString(call[1]) : string.Empty);
                onAttributeChanged?.Invoke(element);
            }

            return JsValue.Undefined;
        }, 2);

        var del = realm.NewMethod("delete", (in call) =>
        {
            var attribute = AttributeNameOf(NameArgument(in call));
            if (attribute != null && element.RemoveAttribute(attribute))
                onAttributeChanged?.Invoke(element);

            return JsValue.Undefined;
        }, 1);

        var keys = realm.NewMethod("keys", (in call) =>
        {
            var names = new List<JsValue>();
            foreach (var (key, _) in element.Attributes)
            {
                if (PropertyNameOf(key.LocalName) is { } propertyName)
                    names.Add(JsValue.String(propertyName));
            }

            return call.Realm.NewArray([.. names]);
        }, 0);

        // The factory is called with undefined as its receiver, as it was before; it closes over the
        // four callbacks and returns the proxy.
        var map = realm.Invoke(factory, JsValue.Undefined, [get, set, del, keys]);
        return map.IsObject ? map : JsValue.Undefined;
    }

    /// <summary>
    /// The property name a trap was asked about, or <see langword="null"/> when it was called with
    /// nothing at all.
    /// </summary>
    /// <remarks>
    /// The rendering is the realm's <c>ToString</c> rather than the handle's, because that is the
    /// observable ECMAScript coercion the engine's own <c>ToString()</c> was performing here. A
    /// missing argument is not coerced: there is nothing to convert, and the traps read that as
    /// "no such property".
    /// </remarks>
    private static string? NameArgument(in JsCall call) =>
        call.Length > 0 ? call.Realm.ToJsString(call[0]) : null;

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
