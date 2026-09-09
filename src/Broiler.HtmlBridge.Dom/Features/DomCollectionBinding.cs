using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

using Broiler.HtmlBridge.Jseal;

// The engine-typed remainder of this file, and it is down to one adapter and the machinery under it.
// DomBridge/DomBridge.FormControlHost.cs is the last caller that builds a collection by handing over a
// script context and a list of engine values — a file input's `files`, which it installs as an engine
// object — so FileList(JSContext, …) and the four members below it stay until that one call migrates.
// The other four adapters have gone with their callers: the interface-registration hub
// (DomBridge/Utilities.DomInterfaces.cs), the query/selector/form-association/node-accessor hosts,
// DomBridge/Utilities.cs and the frame projection in DomBridge.SubDocumentHost.cs all pass a realm now.
// The NamedNodeMap surface is JSEAL too — its supplier, Features/AttributesBinding.cs, migrated — and
// the one engine type left under it is the weak table's key, which must be a reference type.
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

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
/// call to it — which is what <see cref="DomCollection"/> completes the object's property lookup for.
/// A static collection (<c>querySelectorAll</c>, which the specification defines as static) is the
/// same object over a function that returns a fixed list, so one type serves both and the difference
/// is visible at the call site rather than buried in two classes.
/// </para>
/// <para>
/// <b>The collection is an <see cref="IJsExotic"/> handler rather than an engine subclass.</b> A
/// collection's members are not a fixed list — every integer below <c>length</c> and, for an
/// <c>HTMLCollection</c>, every <c>id</c> and <c>name</c> its members carry — so it used to derive
/// from the engine's own object type and override its lookup protocol. It now declares the hook
/// instead: <see cref="IJsRealm.NewExotic"/> takes the handler and the provider owns the protocol.
/// <b>The ordering the subclass established is the ordering the contract mandates</b> — ordinary
/// properties and the prototype chain are consulted first and the handler answers only what they did
/// not — which is what keeps a collection containing an element named <c>item</c> from shadowing its
/// own <c>item()</c> method. That rule used to be written here, in a comment above a
/// <c>base.GetValue</c> call; it is now written in <see cref="IJsExotic"/> and enforced by the realm.
/// </para>
/// <para>
/// <b>The index materialisation moved with it, into the provider, because it was never a fact about
/// the DOM.</b> The indices are real own properties rather than intercepted reads, because an array
/// generic asks whether index <c>i</c> is <em>present</em> before reading it and
/// <c>Object.keys</c>/<c>for…in</c>/spread ask the same way — presence, enumeration and retrieval are
/// separate entry points with no single hook between them. Which of those entry points exist, and
/// that they cannot be served by one override, is a property of an engine's property storage. So the
/// handler now says only how many indexed elements there are
/// (<see cref="IJsExotic.IndexedLength"/>) and what is at each one, and the provider materialises —
/// growing <em>and</em> shrinking, which matters as much: a live collection whose element was removed
/// must stop offering the index rather than keep a stale wrapper at it.
/// </para>
/// <para>
/// <b>The methods are plain JavaScript on a real prototype.</b> Every one of them is expressible in
/// terms of <c>this.length</c> and <c>this[i]</c>, which the live lookup already answers, so
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
    /// Defines the two interfaces and their prototype methods. Runs once per realm, with the
    /// other DOM interface constructors.
    /// </summary>
    public static void RegisterInterfaces(IJsRealm realm)
    {
        realm.EvaluateHostScript("""
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
            """, "interfaces:dom-collections");
    }

    /// <summary>
    /// A <c>NodeList</c> over <paramref name="contents"/>. Pass a function that recomputes for a
    /// live list (<c>childNodes</c>), or one that returns a fixed list for a static one
    /// (<c>querySelectorAll</c>, which DOM §4.2.6 defines as static).
    /// </summary>
    public static JsValue NodeList(IJsRealm realm, Func<List<JsValue>> contents) =>
        Create(realm, "NodeList", contents, namedLookup: null);

    /// <summary>
    /// An <c>HTMLCollection</c> over <paramref name="contents"/>, always live — every collection
    /// specified to be an <c>HTMLCollection</c> is. <paramref name="namedLookup"/> answers the named
    /// getter; it is given the requested name and returns the matching element or
    /// <see langword="null"/>.
    /// </summary>
    public static JsValue HtmlCollection(
        IJsRealm realm, Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup = null) =>
        Create(realm, "HTMLCollection", contents, namedLookup);

    /// <summary>
    /// A <c>StyleSheetList</c> over <paramref name="contents"/> (CSSOM §6.1) — <c>document.styleSheets</c>
    /// and nothing else. Live, and with no named getter: CSSOM declares neither <c>namedItem</c> nor
    /// supported property names on it.
    /// </summary>
    public static JsValue StyleSheetList(IJsRealm realm, Func<List<JsValue>> contents) =>
        Create(realm, "StyleSheetList", contents, namedLookup: null);

    /// <summary>
    /// A <c>FileList</c> over <paramref name="contents"/> (File API §3.2) — a file input's
    /// <c>files</c> and nothing else.
    /// </summary>
    /// <remarks>
    /// Broiler has no file selection, so the list a file input reports is always empty — which is
    /// exactly what a browser reports for an input the user has not touched. The collection is live
    /// over its contents function regardless, so the day a selection exists it needs no second shape.
    /// </remarks>
    public static JsValue FileList(IJsRealm realm, Func<List<JsValue>> contents) =>
        Create(realm, "FileList", contents, namedLookup: null);

    private static JsValue Create(
        IJsRealm realm, string interfaceName, Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup)
    {
        var collection = realm.NewExotic(new DomCollection(contents, namedLookup));

        // A realm that does not yet hold the interface constructors leaves the collection
        // prototype-less rather than failing: it still answers length, the indices and the named
        // lookups the handler serves, and only the shared methods are missing.
        var constructor = realm.GetProperty(realm.Global, interfaceName);
        if (constructor.IsObject)
        {
            var prototype = realm.GetProperty(constructor, "prototype");
            if (prototype.IsObject)
                realm.SetPrototype(collection, prototype);
        }

        return collection;
    }

    /// <summary>
    /// The lookup a collection object completes for itself: its live <c>length</c>, its indexed
    /// elements, and — for an <c>HTMLCollection</c> — the named getter DOM §4.2.10.2 gives it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What is <em>not</em> here is the ordering.</b> The class this replaces called the engine's
    /// base lookup before its own on every path, and said so in a comment; the realm does that now,
    /// for every handler, because <see cref="IJsExotic"/> states it. The consequence is the one worth
    /// restating: a collection holding an element whose <c>id</c> is <c>item</c> still has its
    /// <c>item()</c> method, because the prototype answers before this does.
    /// </para>
    /// <para>
    /// <b><c>length</c> is answered rather than installed</b>, so it stays off <c>Object.keys</c>,
    /// out of <c>for…in</c> and out of <c>Object.getOwnPropertyNames</c> — a browser's is an accessor
    /// on the prototype, not an own property, and the difference is observable exactly there. It is
    /// answered before the named lookup is consulted, as it was before, so a member named
    /// <c>length</c> cannot displace the count.
    /// </para>
    /// </remarks>
    private sealed class DomCollection(Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup) : IJsExotic
    {
        /// <summary>
        /// The contents as of the last <see cref="IndexedLength"/> ask.
        /// </summary>
        /// <remarks>
        /// <b>One walk of the tree per property access, which is what the subclass cost too.</b>
        /// <see cref="IJsExotic.IndexedLength"/> is documented as being asked immediately before the
        /// indices are used, and the provider does exactly that — every read entry point synchronises
        /// first, and synchronising begins by asking for the length. So the list that answer was
        /// computed from is the list the indices and <c>length</c> are then read out of, and a
        /// collection over a whole-document walk is walked once per access rather than once per
        /// element. Nothing runs between the two: the realm is single-threaded and no page script can
        /// interleave with a single property lookup.
        /// </remarks>
        private List<JsValue>? _contents;

        /// <inheritdoc />
        public uint IndexedLength
        {
            get
            {
                _contents = contents();
                return (uint)_contents.Count;
            }
        }

        /// <inheritdoc />
        public bool TryGetIndex(uint index, out JsValue value)
        {
            var items = _contents ??= contents();
            if (index < (uint)items.Count)
            {
                value = items[(int)index];
                return true;
            }

            // Past the end declines, so the read falls through to the ordinary miss — undefined,
            // which is what an out-of-range index answered before.
            value = JsValue.Undefined;
            return false;
        }

        /// <inheritdoc />
        public bool TryGetNamed(string name, out JsValue value)
        {
            if (name == "length")
            {
                value = JsValue.Number((_contents ?? contents()).Count);
                return true;
            }

            if (namedLookup?.Invoke(name) is { } named)
            {
                value = named;
                return true;
            }

            value = JsValue.Undefined;
            return false;
        }

        /// <summary>
        /// Never: a collection has no named setter, so an assignment is an ordinary one exactly as it
        /// was — the subclass overrode no write path.
        /// </summary>
        public bool TrySetNamed(string name, JsValue value) => false;

        /// <summary>
        /// None. A collection's names were never enumerable — the subclass supplied no keys of its
        /// own, so <c>Object.keys(document.forms)</c> is the indices and nothing else — and adding
        /// the supported names here would be browser-correct and a behaviour change rather than a
        /// refactor.
        /// </summary>
        public IReadOnlyList<string> SupportedNames => [];
    }

    // ------------------------------------------------------------------
    //  The one engine-typed adapter left, and the file that pins it.
    // ------------------------------------------------------------------

    /// <inheritdoc cref="FileList(IJsRealm, Func{List{JsValue}})" />
    /// <remarks>
    /// The engine-typed form, and the last of the five that stood here. Its one caller is
    /// <c>DomBridge/DomBridge.FormControlHost.cs</c>, which holds a file input's <c>files</c> as an
    /// engine object because the member it installs it on does; it is not this group's file. Everything
    /// below this point exists to serve this one call, and goes with it.
    /// </remarks>
    public static JSValue FileList(JSContext? context, Func<List<JSValue>> contents) =>
        ToEngineCollection(FileList(RealmFor(context), Adapt(contents)));

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
    public static JsValue NamedNodeMap(
        IJsRealm realm,
        Func<List<JsValue>> contents,
        Func<string, JsValue?> namedLookup,
        NamedNodeMapOperations operations)
    {
        var map = Create(realm, "NamedNodeMap", contents, namedLookup);
        // Keyed on the engine's object because a ConditionalWeakTable needs a reference key and a
        // JsValue is a struct. The handle carries that very object, so the lookup a prototype method
        // performs from its receiver asks the same question it always did.
        OperationsByMap.Add(Runtime.JsInterop.ToEngineObject(map), operations);
        return map;
    }

    /// <summary>The element-dependent members of <c>NamedNodeMap</c>, supplied by the attribute
    /// binding, which owns the attribute write path.</summary>
    internal sealed class NamedNodeMapOperations
    {
        public required JsNativeFunction GetNamedItem { get; init; }
        public required JsNativeFunction GetNamedItemNS { get; init; }
        public required JsNativeFunction SetNamedItem { get; init; }
        public required JsNativeFunction SetNamedItemNS { get; init; }
        public required JsNativeFunction RemoveNamedItem { get; init; }
        public required JsNativeFunction RemoveNamedItemNS { get; init; }
    }

    /// <summary>
    /// Which element each live <c>NamedNodeMap</c> belongs to, so a prototype method can find it from
    /// its receiver. A weak table, so a map that a page has dropped does not pin its element.
    /// </summary>
    private static readonly ConditionalWeakTable<JSObject, NamedNodeMapOperations> OperationsByMap = new();

    /// <summary>
    /// Installs the six host-backed <c>NamedNodeMap</c> methods on the interface prototype. Called
    /// once per realm, after the interface registration above has defined the interface.
    /// </summary>
    /// <remarks>
    /// Each looks its operations up from the receiver, so calling one on something that is not a
    /// <c>NamedNodeMap</c> is a <c>TypeError</c> rather than a silent wrong answer — which is what a
    /// browser gives for an illegal invocation.
    /// <para>
    /// <b>That <c>TypeError</c> is now a real one, and it is this migration's single behaviour
    /// change.</b> The engine-framed version threw a bare <em>string</em> whose text began with
    /// <c>TypeError:</c>, so a page's <c>catch (e)</c> saw <c>typeof e === 'string'</c> with no
    /// <c>name</c> and no <c>message</c>. JSEAL has no "throw this value" — <see cref="IJsCalls.Error"/>
    /// mints against the realm's intrinsic constructor — so the string cannot be reproduced, and the
    /// nearest expressible thing is also what a browser throws. The message text is carried over with
    /// the redundant prefix dropped, which is where the constructor now puts it.
    /// </para>
    /// </remarks>
    public static void RegisterNamedNodeMapOperations(IJsRealm realm)
    {
        var constructor = realm.GetProperty(realm.Global, "NamedNodeMap");
        if (!constructor.IsObject)
            return;

        var prototype = realm.GetProperty(constructor, "prototype");
        if (!prototype.IsObject)
            return;

        Install("getNamedItem", 1, static operations => operations.GetNamedItem);
        Install("getNamedItemNS", 2, static operations => operations.GetNamedItemNS);
        Install("setNamedItem", 1, static operations => operations.SetNamedItem);
        Install("setNamedItemNS", 1, static operations => operations.SetNamedItemNS);
        Install("removeNamedItem", 1, static operations => operations.RemoveNamedItem);
        Install("removeNamedItemNS", 2, static operations => operations.RemoveNamedItemNS);

        void Install(string name, int length, Func<NamedNodeMapOperations, JsNativeFunction> pick) =>
            realm.DefineValue(
                prototype,
                name,
                realm.NewMethod(
                    name,
                    (in call) =>
                    {
                        if (!call.This.IsObject ||
                            !OperationsByMap.TryGetValue(Runtime.JsInterop.ToEngineObject(call.This), out var operations))
                        {
                            throw call.Realm.Error(
                                JsErrorKind.TypeError,
                                $"Failed to execute '{name}' on 'NamedNodeMap': Illegal invocation.");
                        }

                        return pick(operations)(in call);
                    },
                    length));
    }

    /// <summary>The engine object a migrated collection builder minted, for an unmigrated caller.</summary>
    /// <remarks>
    /// <see cref="Runtime.JsInterop"/> is a cast and not a conversion — the handle carries the
    /// engine's own object — so the object handed back is the object the realm minted, and the
    /// <c>ConditionalWeakTable</c>s the bridge keys on collection identity keep finding it.
    /// </remarks>
    private static JSObject ToEngineCollection(JsValue collection) => Runtime.JsInterop.ToEngineObject(collection);

    /// <summary>An engine-typed contents function as the JSEAL one the collection holds.</summary>
    /// <remarks>
    /// Re-wrapped on every read rather than once, because the function is what makes the collection
    /// live: the list it answers with is different each time, and so are the wrappers in it. The one
    /// caller left answers an empty list, and the two non-object arms below are kept anyway so that a
    /// supplier which answered otherwise keeps answering what it answered rather than throwing on the
    /// way through.
    /// </remarks>
    private static Func<List<JsValue>> Adapt(Func<List<JSValue>> contents) =>
        () =>
        {
            var engineValues = contents();
            var handles = new List<JsValue>(engineValues.Count);
            foreach (var value in engineValues)
                handles.Add(Handle(value));

            return handles;
        };

    /// <summary>An engine value a collection holds, as the handle the handler answers with.</summary>
    private static JsValue Handle(JSValue value) =>
        value is JSObject @object ? Runtime.JsInterop.FromEngineObject(@object)
        : value.IsNull ? JsValue.Null
        : JsValue.Undefined;

    /// <summary>
    /// The realm the collection is minted in: the one the registered provider wraps
    /// <paramref name="context"/> as.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Adopted here because the one call site left cannot pass a realm.</b> There were ten of them
    /// handing over the bridge's script context; nine now pass a realm, and the tenth
    /// (<c>DomBridge/DomBridge.FormControlHost.cs</c>) is not this group's to change. Adopting is what
    /// the bridge itself does with the same object at <c>Attach</c> (see <c>DomBridge.Realm.cs</c>)
    /// and what <c>Features/BlobBinding.cs</c> already does for the same reason: a provider that
    /// recognises the context wraps it <em>without owning it</em>, and because every handle carries
    /// the engine's own value a second wrapper over one context mints the objects the first would.
    /// Still cached per context, because a collection factory runs per property read rather than once
    /// per page and must not build a wrapper and its job queue each time.
    /// </para>
    /// <para>
    /// <b>A null context is now a failure rather than a prototype-less collection.</b> The old
    /// builder had nowhere to read the interface prototypes from and shrugged; there is no realm to
    /// mint an <em>object</em> in, so shrugging is not available. The caller passes the bridge's
    /// own context, which <c>Registration.cs</c> assigns on the first line of attach and before any
    /// collection can be built, so this is a diagnosis rather than a path.
    /// </para>
    /// </remarks>
    private static IJsRealm RealmFor(JSContext? context)
    {
        if (context is null)
        {
            throw new InvalidOperationException(
                "A DOM collection was asked for before the bridge was attached to a script context, " +
                "so there is no JavaScript realm to mint it in.");
        }

        return RealmsByContext.GetValue(context, AdoptRealm);
    }

    /// <summary>One adopted realm per context; weakly keyed, so it goes when the context does.</summary>
    private static readonly ConditionalWeakTable<JSContext, IJsRealm> RealmsByContext = new();

    /// <inheritdoc cref="RealmFor" />
    private static IJsRealm AdoptRealm(JSContext context)
    {
        foreach (var provider in JsEngineRegistry.All)
        {
            if (provider is IJsRealmAdoption adoption &&
                adoption.TryAdopt(context, out var realm) &&
                realm is not null)
            {
                return realm;
            }
        }

        throw new InvalidOperationException(
            "No registered JavaScript engine provider recognised the script context the DOM " +
            "collection builder was handed. A host must reference an engine provider assembly — " +
            "Broiler.HtmlBridge.Jseal.BroilerJs for Broiler.JS — and that assembly registers itself " +
            "when it is loaded.");
    }
}
