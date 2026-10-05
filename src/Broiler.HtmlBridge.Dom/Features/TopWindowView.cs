using System.Runtime.CompilerServices;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The top window as a frame of its own origin has it, as <c>top</c> and as <c>parent</c>: the global
/// object, seen through a <c>Proxy</c> that answers the top window's own members itself.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not the global object itself.</b> Every document here shares one, and it answers for the
/// window whose script is running: a frame's script has its own <c>document</c>, <c>window</c>,
/// <c>self</c>, <c>parent</c>, <c>top</c> and <c>postMessage</c> swapped onto it, and its
/// <c>location</c> and <c>name</c> are accessors that answer the frame's. Handed the global as
/// <c>top</c>, a frame reached itself: <c>top.location</c> was its own Location.
/// </para>
/// <para>
/// <b>Everything else is the global's.</b> The page's functions and variables are on it, and a frame
/// calls them as <c>parent.fn()</c>; reads, writes, <c>in</c>, enumeration, definition and deletion
/// all go through to it. Only the names the host lists are answered by the host, read and written.
/// </para>
/// <para>
/// The target is an empty object rather than the global, so that the proxy invariants are checked
/// against an object with no fixed properties: a descriptor is reported configurable, which the
/// global's own fixed ones (<c>undefined</c>, <c>NaN</c>) are not, and that is the only difference a
/// script could see.
/// </para>
/// </remarks>
internal static class TopWindowView
{
    /// <summary>
    /// The JavaScript half: given the global object, a factory that takes the names the host answers
    /// and its two callbacks, and returns the view. A compile-time constant of this assembly,
    /// evaluated once per realm.
    /// </summary>
    private const string FactorySource = @"
(function (global) {
  'use strict';
  return function (names, lookup, assign) {
    var own = new Set(names);
    function isOwn(name) { return typeof name === 'string' && own.has(name); }
    return new Proxy(Object.create(null), {
      get: function (target, name) {
        return isOwn(name) ? lookup(name) : Reflect.get(global, name);
      },
      set: function (target, name, value) {
        if (isOwn(name)) { assign(name, value); return true; }
        return Reflect.set(global, name, value);
      },
      has: function (target, name) {
        return isOwn(name) || Reflect.has(global, name);
      },
      getOwnPropertyDescriptor: function (target, name) {
        if (isOwn(name)) return { value: lookup(name), writable: true, enumerable: true, configurable: true };
        var descriptor = Reflect.getOwnPropertyDescriptor(global, name);
        if (descriptor) descriptor.configurable = true;
        return descriptor;
      },
      defineProperty: function (target, name, descriptor) {
        return isOwn(name) ? false : Reflect.defineProperty(global, name, descriptor);
      },
      deleteProperty: function (target, name) {
        return isOwn(name) ? false : Reflect.deleteProperty(global, name);
      },
      ownKeys: function () { return Reflect.ownKeys(global); },
      getPrototypeOf: function () { return Reflect.getPrototypeOf(global); },
      setPrototypeOf: function (target, prototype) { return prototype === Reflect.getPrototypeOf(global); },
      isExtensible: function () { return true; },
      preventExtensions: function () { return false; }
    });
  };
})";

    // Once per realm, as CrossOriginWindowView's factory is, and weakly keyed for the same reason.
    private static readonly ConditionalWeakTable<IJsRealm, StrongBox<JsValue>> Factories = new();

    /// <summary>
    /// Builds the view of <paramref name="global"/>, or answers a non-object when the realm has no
    /// <c>Proxy</c> to build one from.
    /// </summary>
    /// <param name="realm">The realm the proxy is minted in.</param>
    /// <param name="global">The global object, which is the top window.</param>
    /// <param name="names">An array of the names <paramref name="lookup"/> and <paramref name="assign"/> answer for.</param>
    /// <param name="lookup">Called with one of <paramref name="names"/>: answers the top window's member.</param>
    /// <param name="assign">Called with one of <paramref name="names"/> and a value: performs the assignment.</param>
    internal static JsValue Build(IJsRealm realm, JsValue global, JsValue names, JsValue lookup, JsValue assign)
    {
        var factory = FactoryFor(realm, global);
        return factory.IsFunction ? realm.Invoke(factory, JsValue.Undefined, [names, lookup, assign]) : JsValue.Undefined;
    }

    private static JsValue FactoryFor(IJsRealm realm, JsValue global)
    {
        if (Factories.TryGetValue(realm, out var cached))
            return cached.Value;

        var outer = realm.EvaluateHostScript(FactorySource, "broiler:top-window-view");
        if (!outer.IsFunction)
            return JsValue.Undefined;

        var factory = realm.Invoke(outer, JsValue.Undefined, [global]);
        if (!factory.IsFunction)
            return JsValue.Undefined;

        Factories.AddOrUpdate(realm, new StrongBox<JsValue>(factory));
        return factory;
    }
}
