using System.Runtime.CompilerServices;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The object a script is handed for a window, or a <c>Location</c>, of another origin: one that
/// answers the few members HTML lets a cross-origin script use and throws a <c>SecurityError</c> for
/// every other (HTML §7.2.3, "Cross-origin objects").
/// </summary>
/// <remarks>
/// <para>
/// <b>A <c>Proxy</c>, because every other member has to throw.</b> An object minted with members
/// answers <c>undefined</c> for a member it lacks, and inherits <c>toString</c>, <c>constructor</c>
/// and the rest from <c>Object.prototype</c>. What a browser does instead is refuse the access itself:
/// reading, writing, defining, deleting, <c>in</c> and <c>String(w)</c> all throw, and the prototype
/// is <c>null</c>. So the traps ask the host for each name, and the host answers or throws.
/// </para>
/// <para>
/// <b>Four keys read as <c>undefined</c> instead of throwing</b> (CrossOriginPropertyFallback):
/// <c>then</c>, so that resolving a promise with a window does not throw, and
/// <c>@@toStringTag</c>, <c>@@hasInstance</c> and <c>@@isConcatSpreadable</c>, which the language
/// reads in the course of ordinary operations on any object.
/// </para>
/// <para>
/// <b>Not a wall around the frame's realm.</b> Every document here runs in one JavaScript context,
/// so a frame shares its built-ins with the page that embeds it. What this keeps from a script is
/// the frame's <em>window</em>: its document, and what its scripts published on it.
/// </para>
/// </remarks>
internal static class CrossOriginWindowView
{
    /// <summary>The message a refused access throws with, as Chromium words it without the origins.</summary>
    internal const string BlockedMessage = "Blocked a frame from accessing a cross-origin frame.";

    /// <summary>
    /// The JavaScript half: given the host's refusal, a factory that takes the three host callbacks
    /// of one object and returns its proxy. A compile-time constant of this assembly, evaluated once
    /// per realm.
    /// </summary>
    private const string FactorySource = @"
(function (refuse) {
  'use strict';
  var fallbackSymbols = [Symbol.toStringTag, Symbol.hasInstance, Symbol.isConcatSpreadable];
  function isFallback(name) {
    return name === 'then' || fallbackSymbols.indexOf(name) !== -1;
  }
  return function (lookup, assign, keys) {
    return new Proxy(Object.create(null), {
      get: function (target, name) {
        if (isFallback(name)) return undefined;
        if (typeof name !== 'string') return refuse();
        return lookup(name);
      },
      set: function (target, name, value) {
        if (typeof name !== 'string') return refuse();
        assign(name, value);
        return true;
      },
      has: function (target, name) {
        if (isFallback(name)) return true;
        if (typeof name !== 'string') return refuse();
        lookup(name);
        return true;
      },
      getOwnPropertyDescriptor: function (target, name) {
        if (isFallback(name)) return { value: undefined, writable: false, enumerable: false, configurable: true };
        if (typeof name !== 'string') return refuse();
        return { value: lookup(name), writable: false, enumerable: false, configurable: true };
      },
      ownKeys: function () { return keys().concat(['then'], fallbackSymbols); },
      defineProperty: function () { return refuse(); },
      deleteProperty: function () { return refuse(); },
      getPrototypeOf: function () { return null; },
      setPrototypeOf: function (target, prototype) { return prototype === null; },
      isExtensible: function () { return true; },
      preventExtensions: function () { return false; }
    });
  };
})";

    // Once per realm, as DatasetBinding's factory is, and weakly keyed for the same reason: a finished
    // document's realm stays collectable.
    private static readonly ConditionalWeakTable<IJsRealm, StrongBox<JsValue>> Factories = new();

    /// <summary>
    /// Builds a cross-origin object over three host callbacks, or answers a non-object when the realm
    /// has no <c>Proxy</c> to build one from.
    /// </summary>
    /// <param name="realm">The realm the proxy is minted in.</param>
    /// <param name="lookup">
    /// Called with a property name: answers the value of a member a cross-origin script may read, and
    /// throws <c>SecurityError</c> for any other.
    /// </param>
    /// <param name="assign">
    /// Called with a property name and a value: performs the one assignment a cross-origin script may
    /// make, and throws <c>SecurityError</c> for any other.
    /// </param>
    /// <param name="keys">Answers the array of the names <paramref name="lookup"/> answers.</param>
    internal static JsValue Build(IJsRealm realm, JsValue lookup, JsValue assign, JsValue keys)
    {
        var factory = FactoryFor(realm);
        return factory.IsFunction ? realm.Invoke(factory, JsValue.Undefined, [lookup, assign, keys]) : JsValue.Undefined;
    }

    /// <summary>The <c>SecurityError</c> every refused access throws.</summary>
    internal static System.Exception Refusal(IJsRealm realm) => realm.DomError("SecurityError", BlockedMessage);

    private static JsValue FactoryFor(IJsRealm realm)
    {
        if (Factories.TryGetValue(realm, out var cached))
            return cached.Value;

        var outer = realm.EvaluateHostScript(FactorySource, "broiler:cross-origin-view");
        if (!outer.IsFunction)
            return JsValue.Undefined;

        var refuse = realm.NewMethod("refuse", (in call) => throw Refusal(call.Realm), 0);
        var factory = realm.Invoke(outer, JsValue.Undefined, [refuse]);
        if (!factory.IsFunction)
            return JsValue.Undefined;

        Factories.AddOrUpdate(realm, new StrongBox<JsValue>(factory));
        return factory;
    }
}
