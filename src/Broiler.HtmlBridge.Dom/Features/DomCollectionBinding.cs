using System;
using System.Collections.Generic;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>NodeList</c> and <c>HTMLCollection</c> — the two DOM collection interfaces (DOM §4.2.10 and
/// §4.2.10.2) — and CSSOM's <c>StyleSheetList</c> (§6.1), as real interfaces with real prototypes
/// rather than the plain JavaScript arrays the bridge used to hand back.
/// </summary>
/// <remarks>
/// <para>
/// An array is wrong in three separate ways, and the third is the one that silently changes results.
/// <c>NodeList</c> and <c>HTMLCollection</c> were not defined at all, so <c>instanceof</c> was a
/// <c>ReferenceError</c> and <c>childNodes.constructor.name</c> answered <c>"Array"</c>.
/// <c>item()</c> and <c>namedItem()</c> did not exist, while <c>map</c>, <c>filter</c> and
/// <c>slice</c> did — the opposite of a browser both ways round, so feature-detecting code branched
/// wrongly in both directions. And <b>an array is a snapshot</b>: <c>childNodes</c> and
/// <c>getElementsByTagName</c> are specified as <em>live</em>, so
/// <c>var kids = el.childNodes; el.appendChild(x); kids.length</c> grows in a browser and did not
/// here. That last one produces a wrong number rather than an error, which is why it could sit under
/// passing tests.
/// </para>
/// <para>
/// <b>Liveness is the whole design.</b> A collection object holds the <em>function</em> that
/// produces its contents, not the contents, and answers <c>length</c> and every index from a fresh
/// call to it — which is what <see cref="DomCollection.GetValue"/> overrides property access for. A
/// static collection (<c>querySelectorAll</c>, which the specification defines as static) is the
/// same object over a function that returns a fixed list, so one type serves both and the difference
/// is visible at the call site rather than buried in two classes.
/// </para>
/// <para>
/// <b>The methods are plain JavaScript on a real prototype.</b> Every one of them is expressible in
/// terms of <c>this.length</c> and <c>this[i]</c>, which the live accessor already answers, so
/// writing them in JavaScript costs nothing and buys the parts that are awkward from C#:
/// <c>Symbol.iterator</c>, the generator-based <c>entries</c>/<c>keys</c>/<c>values</c>, and
/// correct <c>this</c> handling for a method held on the prototype rather than on each instance. It
/// also means <c>NodeList.prototype.item</c> exists and is shared, as Web IDL requires — a page
/// reading it off the prototype finds the same function the instance uses.
/// </para>
/// <para>
/// This is roadmap track 6 action 1, "establish real interface prototypes and Web IDL collection
/// behavior <em>before</em> adding more compatibility-only constructor globals" — so these two are
/// deliberately not the <c>@@hasInstance</c> shims the per-tag <c>HTML*Element</c> interfaces use.
/// An instance's prototype really is <c>NodeList.prototype</c>, so <c>instanceof</c> answers through
/// the chain rather than through a hook.
/// </para>
/// </remarks>
internal static class DomCollectionBinding
{
    /// <summary>
    /// Defines the two interfaces and their prototype methods. Runs once per context, with the
    /// other DOM interface constructors.
    /// </summary>
    public static void RegisterInterfaces(JSContext context)
    {
        context.Eval("""
            // Not constructible, as in a browser: a collection comes from the DOM, never from `new`.
            function NodeList() { throw new TypeError('Illegal constructor'); }
            function HTMLCollection() { throw new TypeError('Illegal constructor'); }
            // CSSOM §6.1. Not a NodeList and not an HTMLCollection — it holds stylesheet objects
            // rather than nodes — but the same indexed-property interface, so it shares the
            // machinery below and carries only the two members CSSOM gives it.
            function StyleSheetList() { throw new TypeError('Illegal constructor'); }
            // DOM §4.9.1. An element's `attributes`, and the one collection here whose members can
            // mutate the tree — see the host operations installed on its prototype below.
            function NamedNodeMap() { throw new TypeError('Illegal constructor'); }
            // File API §3.2. `<input type=file>.files`, and nothing else — which is why it is here
            // rather than with Blob and File: it is an indexed collection, and this is where the
            // indexed-collection machinery lives.
            function FileList() { throw new TypeError('Illegal constructor'); }

            (function () {
                // Every method here is written against `this.length` and `this[i]` only. The host
                // answers both from the collection's live contents, so a method defined once on the
                // prototype is correct for a live and a static collection alike, and needs to know
                // which it is holding no more than a caller does.
                // Enumerable, which is what Web IDL says of an interface's members and what a
                // browser has: `for (var k in el.childNodes)` yields `item`, `forEach` and the rest
                // beside the indices. They were non-enumerable here, so it yielded only indices.
                function define(target, name, value) {
                    Object.defineProperty(target, name, {
                        value: value, writable: true, enumerable: true, configurable: true
                    });
                }

                function item(index) {
                    // Out of range is null, not undefined — the two are distinguishable and DOM
                    // §4.2.10 specifies null.
                    var i = index >>> 0;
                    return i < this.length ? this[i] : null;
                }

                function forEach(callback, thisArg) {
                    if (typeof callback !== 'function')
                        throw new TypeError('Failed to execute forEach: the callback is not a function.');
                    // `this.length` is re-read each step: a callback that mutates the tree changes a
                    // live collection underneath the walk, and the specification's iteration order
                    // is over the collection as it is, not as it was.
                    for (var i = 0; i < this.length; i++)
                        callback.call(thisArg, this[i], i, this);
                }

                function values() {
                    var list = this, i = 0;
                    return makeIterator(function () {
                        return i < list.length ? { value: list[i++], done: false } : { value: undefined, done: true };
                    });
                }

                function keys() {
                    var list = this, i = 0;
                    return makeIterator(function () {
                        return i < list.length ? { value: i++, done: false } : { value: undefined, done: true };
                    });
                }

                function entries() {
                    var list = this, i = 0;
                    return makeIterator(function () {
                        if (i >= list.length) return { value: undefined, done: true };
                        var pair = [i, list[i]];
                        i++;
                        return { value: pair, done: false };
                    });
                }

                // A hand-rolled iterator rather than a generator, so this does not depend on
                // generator support in the host to hand back something `for...of` and spread accept.
                function makeIterator(next) {
                    var iterator = { next: next };
                    iterator[Symbol.iterator] = function () { return this; };
                    return iterator;
                }

                [NodeList, HTMLCollection, StyleSheetList, NamedNodeMap, FileList].forEach(function (ctor) {
                    define(ctor.prototype, 'item', item);
                    define(ctor.prototype, Symbol.iterator, values);
                });

                // NamedNodeMap's members all come from C# (see NamedNodeMapOperations): even
                // getNamedItem cannot be written as `this[name]` the way HTMLCollection's namedItem
                // is, because an interface member wins the property lookup over a named one — an
                // element carrying `length="x"` has `attributes.length === 3`, while
                // `getNamedItem('length')` must still hand back the attribute. Measured.

                // NodeList is iterable and HTMLCollection is NOT (DOM §4.2.10.2 declares no
                // iterable<> on it), so only NodeList gets the iteration helpers. HTMLCollection
                // keeps Symbol.iterator above because a browser's does too — it comes from the
                // indexed-property support, not from an iterable declaration — but a page that
                // calls htmlCollection.forEach gets the TypeError a browser gives it rather than a
                // convenience this engine invented.
                define(NodeList.prototype, 'forEach', forEach);
                define(NodeList.prototype, 'entries', entries);
                define(NodeList.prototype, 'keys', keys);
                define(NodeList.prototype, 'values', values);

                // HTMLCollection's named getter (DOM §4.2.10.2): by id, and by name for the
                // elements HTML gives a name to. The host answers the property lookup; this is the
                // method spelling of the same thing.
                define(HTMLCollection.prototype, 'namedItem', function (name) {
                    var value = this[String(name)];
                    return value === undefined ? null : value;
                });
            })();
            """);
    }

    /// <summary>
    /// A <c>NodeList</c> over <paramref name="contents"/>. Pass a function that recomputes for a
    /// live list (<c>childNodes</c>), or one that returns a fixed list for a static one
    /// (<c>querySelectorAll</c>, which DOM §4.2.6 defines as static).
    /// </summary>
    public static JSValue NodeList(JSContext? context, Func<List<JSValue>> contents) =>
        Create(context, "NodeList", contents, namedLookup: null);

    /// <summary>
    /// An <c>HTMLCollection</c> over <paramref name="contents"/>, always live — every collection
    /// specified to be an <c>HTMLCollection</c> is. <paramref name="namedLookup"/> answers the named
    /// getter; it is given the requested name and returns the matching element or
    /// <see langword="null"/>.
    /// </summary>
    public static JSValue HtmlCollection(
        JSContext? context, Func<List<JSValue>> contents, Func<string, JSValue?>? namedLookup = null) =>
        Create(context, "HTMLCollection", contents, namedLookup);

    /// <summary>
    /// A <c>StyleSheetList</c> over <paramref name="contents"/> (CSSOM §6.1) — <c>document.styleSheets</c>
    /// and nothing else. Live, and with no named getter: CSSOM declares neither <c>namedItem</c> nor
    /// supported property names on it.
    /// </summary>
    public static JSValue StyleSheetList(JSContext? context, Func<List<JSValue>> contents) =>
        Create(context, "StyleSheetList", contents, namedLookup: null);

    /// <summary>
    /// A <c>FileList</c> over <paramref name="contents"/> (File API §3.2) — a file input's
    /// <c>files</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// Broiler has no file selection, so the list a file input reports is always empty — which is
    /// exactly what a browser reports for an input the user has not touched. The collection is live
    /// over its contents function regardless, so the day a selection exists it needs no second shape.
    /// </remarks>
    public static JSValue FileList(JSContext? context, Func<List<JSValue>> contents) =>
        Create(context, "FileList", contents, namedLookup: null);

    /// <summary>
    /// A <c>NamedNodeMap</c> over <paramref name="contents"/> (DOM §4.9.1) — an element's
    /// <c>attributes</c> and nothing else. Live, with the qualified-name getter the interface
    /// declares.
    /// </summary>
    /// <remarks>
    /// The members that mutate, or that need the owning element rather than the collection, cannot
    /// be written against <c>this.length</c> and <c>this[i]</c> the way every other method here is,
    /// so they are host functions rather than JavaScript. They still live on the
    /// <em>prototype</em>, shared, as Web IDL requires: each reads its element back from
    /// <paramref name="operations"/> keyed on the receiver, so no per-instance slot appears on the
    /// object and <c>Object.getOwnPropertyNames(el.attributes)</c> stays the indices alone.
    /// </remarks>
    public static JSValue NamedNodeMap(
        JSContext? context,
        Func<List<JSValue>> contents,
        Func<string, JSValue?> namedLookup,
        NamedNodeMapOperations operations)
    {
        var map = Create(context, "NamedNodeMap", contents, namedLookup);
        if (map is JSObject instance)
            OperationsByMap.Add(instance, operations);
        return map;
    }

    /// <summary>The element-dependent members of <c>NamedNodeMap</c>, supplied by the attribute
    /// binding, which owns the attribute write path.</summary>
    internal sealed class NamedNodeMapOperations
    {
        public required Func<Arguments, JSValue> GetNamedItem { get; init; }
        public required Func<Arguments, JSValue> GetNamedItemNS { get; init; }
        public required Func<Arguments, JSValue> SetNamedItem { get; init; }
        public required Func<Arguments, JSValue> SetNamedItemNS { get; init; }
        public required Func<Arguments, JSValue> RemoveNamedItem { get; init; }
        public required Func<Arguments, JSValue> RemoveNamedItemNS { get; init; }
    }

    /// <summary>
    /// Which element each live <c>NamedNodeMap</c> belongs to, so a prototype method can find it from
    /// its receiver. A weak table, so a map that a page has dropped does not pin its element.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JSObject, NamedNodeMapOperations>
        OperationsByMap = new();

    /// <summary>
    /// Installs the six host-backed <c>NamedNodeMap</c> methods on the interface prototype. Called
    /// once per context, after <see cref="RegisterInterfaces"/> has defined the interface.
    /// </summary>
    /// <remarks>
    /// Each looks its operations up from the receiver, so calling one on something that is not a
    /// <c>NamedNodeMap</c> is a <c>TypeError</c> rather than a silent wrong answer — which is what a
    /// browser gives for an illegal invocation.
    /// </remarks>
    public static void RegisterNamedNodeMapOperations(JSContext context)
    {
        if (context["NamedNodeMap"] is not JSObject constructor ||
            constructor[(KeyString)"prototype"] is not JSObject prototype)
            return;

        Install("getNamedItem", 1, static operations => operations.GetNamedItem);
        Install("getNamedItemNS", 2, static operations => operations.GetNamedItemNS);
        Install("setNamedItem", 1, static operations => operations.SetNamedItem);
        Install("setNamedItemNS", 1, static operations => operations.SetNamedItemNS);
        Install("removeNamedItem", 1, static operations => operations.RemoveNamedItem);
        Install("removeNamedItemNS", 2, static operations => operations.RemoveNamedItemNS);

        void Install(string name, int length, Func<NamedNodeMapOperations, Func<Arguments, JSValue>> pick) =>
            prototype.FastAddValue(
                name,
                new DomFunction(
                    (in Arguments a) =>
                    {
                        if (a.This is not JSObject receiver || !OperationsByMap.TryGetValue(receiver, out var operations))
                            throw new JSException(new JSString(
                                $"TypeError: Failed to execute '{name}' on 'NamedNodeMap': Illegal invocation."));
                        return pick(operations)(a);
                    },
                    name,
                    length),
                JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private static JSValue Create(
        JSContext? context, string interfaceName, Func<List<JSValue>> contents, Func<string, JSValue?>? namedLookup)
    {
        var collection = new DomCollection(contents, namedLookup);

        // Before the bridge is attached there is no realm holding the interfaces, so the collection
        // is left prototype-less rather than failing: it still answers length, indices and the
        // property lookups the host serves, and only the shared methods are missing.
        if (PrototypeOf(context, interfaceName) is { } prototype)
            collection.BasePrototypeObject = prototype;

        return collection;
    }

    private static JSObject? PrototypeOf(JSContext? context, string interfaceName)
    {
        if (context is null)
            return null;

        return context[interfaceName] is JSObject constructor
            ? constructor[(KeyString)"prototype"] as JSObject
            : null;
    }

    /// <summary>
    /// The collection object itself: an ordinary <see cref="JSObject"/> whose indexed properties and
    /// <c>length</c> are answered from its contents function rather than stored.
    /// </summary>
    private sealed class DomCollection(Func<List<JSValue>> contents, Func<string, JSValue?>? namedLookup) : JSObject
    {
        private int _materialized;

        /// <summary>
        /// Brings the object's own indexed properties up to date with the contents function, and
        /// returns the current count.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The collection could instead answer indices purely by intercepting reads, and the first
        /// attempt here did — which worked for everything written against <c>this[i]</c> and failed
        /// for everything written against the object. <c>Array.prototype.map.call(list, …)</c> read
        /// <c>length</c> correctly and then produced a hole for every element, because an array
        /// generic asks whether index <c>i</c> is <em>present</em> before reading it, and an object
        /// with no own indexed properties answers no. The same is true of <c>Object.keys</c>,
        /// <c>for…in</c>, and spread. There is no single read hook to intercept — presence,
        /// enumeration and retrieval are separate entry points — so the indices are made real
        /// instead, and every generic algorithm then works on the collection without knowing what it
        /// is. That is also the shape the bridge's other live collection (the CSSOM
        /// <c>cssRules</c> list) already uses.
        /// </para>
        /// <para>
        /// Called from each read entry point rather than on mutation, because a live collection has
        /// no mutation of its own to hook: what it reflects is the tree, and the read is the only
        /// moment it is known to matter.
        /// </para>
        /// </remarks>
        private int Sync()
        {
            var items = contents();
            for (var i = 0; i < items.Count; i++)
                this[(uint)i] = items[i];

            // Shrinking matters as much as growing: a live collection whose element was removed must
            // stop offering the index, not keep a stale wrapper at it.
            for (var i = items.Count; i < _materialized; i++)
                GetElements().RemoveAt((uint)i);

            _materialized = items.Count;
            return items.Count;
        }

        public override JSValue GetValue(uint key, JSValue receiver, bool throwError = true)
        {
            Sync();
            return base.GetValue(key, receiver, throwError);
        }

        protected override JSValue GetValue(KeyString key, JSValue receiver, bool throwError = true)
        {
            var name = key.Value.ToString();

            // length is answered rather than materialized, so it stays off Object.keys and out of
            // for…in — a browser's is an accessor on the prototype, not an own property, and the
            // difference is observable exactly there.
            if (name == "length")
                return new JSNumber(contents().Count);

            Sync();

            // The prototype's methods, and anything else, before the named getter: Web IDL consults
            // named properties only when the object and its prototype chain do not already answer,
            // so a collection holding an element named "item" still has its item() method.
            var resolved = base.GetValue(key, receiver, false);
            if (resolved != null && !resolved.IsUndefined)
                return resolved;

            if (namedLookup?.Invoke(name) is { } named)
                return named;

            return base.GetValue(key, receiver, throwError);
        }

        public override JSValue HasProperty(JSValue propertyKey)
        {
            var name = propertyKey.ToString();
            if (name == "length")
                return JSBoolean.True;

            Sync();
            if (base.HasProperty(propertyKey) is JSBoolean { BooleanValue: true } present)
                return present;

            return namedLookup?.Invoke(name) is not null ? JSBoolean.True : JSBoolean.False;
        }

        public override IElementEnumerator GetAllKeys(bool showEnumerableOnly = true, bool inherited = true)
        {
            Sync();
            return base.GetAllKeys(showEnumerableOnly, inherited);
        }
    }
}
