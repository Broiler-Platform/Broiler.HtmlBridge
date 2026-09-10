using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The Web Storage areas (HTML §12.2) — <c>localStorage</c> and <c>sessionStorage</c>, each a
/// <c>Storage</c> object over an in-memory string map — co-located as an HtmlBridge feature module
/// (Phase 3). A fully self-contained slice: the store is a plain dictionary and the callbacks touch
/// no bridge state, so — like <c>ClassListBinding</c> — it is an <b>internal static class with no
/// host contract</b>. Was the bridge's <c>BuildLocalStorageObject</c> plus
/// <c>JsUtilitiesGetItem029Core</c>..<c>Clear032Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// A capture keeps each area for the life of the page and never persists it: there is no profile to
/// write to, and a page that reads back what it just wrote — which is what most of this API is used
/// for — gets a consistent answer either way.
/// </para>
/// <para>
/// Both areas exist because a missing one is not an empty one. <c>sessionStorage</c> was never
/// registered, and since <c>window</c> IS the global object that made the unqualified
/// <c>sessionStorage</c> a <c>ReferenceError</c> rather than an undefined property — which aborts
/// the whole script, not the statement that read it. <c>www.mediawiki.org</c> loads its modules
/// through one <c>load.php</c> bundle, so the abort took the entire bundle with it (ResourceLoader,
/// the Vector skin's scripts and every module queued behind them) off one identifier.
/// </para>
/// <para>
/// <b>This was the last of the six lookup-completing objects to name an engine type, and the gap that
/// kept it here was a missing hook rather than a missing realm.</b> <c>Storage</c> is the only one of
/// the six whose behaviour includes a <em>deletion</em> — <c>delete localStorage.foo</c> takes the
/// item out of the area, so <c>getItem</c> stops answering for it and <c>length</c> and <c>key(n)</c>
/// stop counting it — and <see cref="IJsExotic"/> declared a named read, an indexed read and a named
/// write and no delete. Converting without one would have left the ordinary property deleted and the
/// item still in the store, which is a wrong answer rather than a missing feature. The hook is
/// <see cref="IJsExoticDelete"/>, and <see cref="StorageArea"/> implements both.
/// </para>
/// <para>
/// <b>The area no longer mirrors its items into ordinary properties; it answers for them.</b> That is
/// what a legacy platform object with named getters and setters is, and it is what makes the two
/// spellings genuinely one item rather than two copies kept in step. It also settles by construction
/// two things the mirror had to arrange by hand: an interface member outranks a key of the same name
/// because ordinary properties are consulted first, and a value assigned as a property is stored as
/// the string HTML §12.2.2 requires because the handler coerces it on the way in rather than after a
/// raw copy has already been written.
/// </para>
/// <para>
/// <b>One spelling still does not reach the area, and it is a third gap in the same contract.</b> Both
/// engines route an integer-index key to the indexed hooks, and <see cref="IJsExotic"/> has no indexed
/// <em>write</em> hook and no way for a handler to declare that it has no indexed properties at all.
/// <c>Storage</c> has named property getters and setters and no indexed ones, so <c>localStorage[8]</c>
/// is a name like any other and is treated here as an index by both. Reported rather than worked
/// around, as the delete hook was; <c>WebStorageTests.ADigitOnlyKeyIsANamedPropertyLikeAnyOther</c>
/// carries the case.
/// </para>
/// </remarks>
internal static class WebStorageBinding
{
    /// <summary>
    /// The <c>Storage</c> interface members. A page's key by the same name must not overwrite the
    /// method — see <see cref="StorageArea.TrySetNamed"/>.
    /// </summary>
    private static readonly HashSet<string> InterfaceMembers = new(StringComparer.Ordinal)
    {
        "getItem", "setItem", "removeItem", "clear", "key", "length",
    };

    /// <summary>
    /// Builds one storage area. Call it once per area — <c>localStorage</c> and
    /// <c>sessionStorage</c> are separate areas and must not share a backing store.
    /// </summary>
    /// <param name="realm">The realm the area's six members are minted in.</param>
    public static JsValue BuildStorage(IJsRealm realm)
    {
        var storage = new StorageArea(realm);
        var area = realm.NewExotic(storage);

        // Non-enumerable, as they are in a browser: there the members live on Storage.prototype and
        // only the stored keys are own properties, so `for (var k in storage)` and
        // `Object.keys(storage)` yield keys alone. Bridge objects carry their members directly
        // (see RegisterDomInterfaceConstructors), so hiding them from enumeration is what keeps a
        // page that iterates a storage area from finding four methods among its keys.
        realm.DefineValue(area, "getItem",
            realm.NewMethod("getItem", (in call) => GetItem(storage, in call), 1),
            JsPropertyFlags.NonEnumerable);

        realm.DefineValue(area, "setItem",
            realm.NewMethod("setItem", (in call) => SetItem(storage, in call), 2),
            JsPropertyFlags.NonEnumerable);

        realm.DefineValue(area, "removeItem",
            realm.NewMethod("removeItem", (in call) => RemoveItem(storage, in call), 1),
            JsPropertyFlags.NonEnumerable);

        realm.DefineValue(area, "clear",
            realm.NewMethod("clear", (in call) => Clear(storage, in call), 0),
            JsPropertyFlags.NonEnumerable);

        realm.DefineValue(area, "key",
            realm.NewMethod("key", (in call) => Key(storage, in call), 1),
            JsPropertyFlags.NonEnumerable);

        realm.DefineAccessor(area, "length",
            (in _) => JsValue.Number(storage.Count),
            null,
            JsPropertyFlags.NonEnumerable);

        return area;
    }

    private static JsValue GetItem(StorageArea storage, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        return storage.TryGet(call.Realm.ToJsString(call[0]), out var val) ? JsValue.String(val) : JsValue.Null;
    }

    private static JsValue SetItem(StorageArea storage, in JsCall call)
    {
        if (call.Length >= 2)
            storage.Put(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));

        return JsValue.Undefined;
    }

    private static JsValue RemoveItem(StorageArea storage, in JsCall call)
    {
        if (call.Length > 0)
            storage.Remove(call.Realm.ToJsString(call[0]));

        return JsValue.Undefined;
    }

    private static JsValue Clear(StorageArea storage, in JsCall _)
    {
        storage.RemoveAll();
        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>key(n)</c> — the <c>n</c>th key in insertion order, or <c>null</c> past the end. Paired
    /// with <c>length</c> it is how a page enumerates an area it did not write itself; MediaWiki's
    /// <c>ext.centralNotice</c> key-value store sweeps its own keys exactly that way.
    /// </summary>
    private static JsValue Key(StorageArea storage, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;

        var index = call.Realm.ToNumber(call[0]);
        if (double.IsNaN(index) || index < 0 || index >= storage.Count)
            return JsValue.Null;

        return JsValue.String(storage.KeyAt((int)index));
    }

    /// <summary>
    /// A storage area: the ordered key/value map, and the host-completed lookup that makes
    /// <c>storage.foo</c> and <c>storage["foo"]</c> address the same item as
    /// <c>getItem</c>/<c>setItem</c> do.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Storage is a legacy platform object with named property getters and setters (HTML §12.2.2),
    /// so the two spellings are one item in a browser and pages use them interchangeably. The area
    /// answers for its items rather than mirroring them into ordinary properties: a read the object's
    /// own members did not satisfy reaches <see cref="TryGetNamed"/>, an assignment is claimed by
    /// <see cref="TrySetNamed"/> before any property is created, and a deletion reaches
    /// <see cref="TryDeleteNamed"/>. Nothing has to be kept in step, because there is only one copy.
    /// </para>
    /// <para>
    /// <b>The six interface members are declined at every hook, which is what keeps a key named like
    /// one from costing the page the member.</b> They are ordinary properties of the area — in a
    /// browser they live on <c>Storage.prototype</c> and only the keys are own properties, but bridge
    /// objects carry their members directly — and ordinary properties are consulted first, so a stored
    /// <c>getItem</c> stays readable through <c>getItem</c>, <c>key</c> and <c>length</c> while the
    /// method it collides with goes on being callable.
    /// </para>
    /// </remarks>
    private sealed class StorageArea(IJsRealm realm) : IJsExotic, IJsExoticDelete
    {
        private readonly Dictionary<string, string> _store = new(StringComparer.Ordinal);

        /// <summary>Insertion order, which is the order <c>key(n)</c> reports.</summary>
        private readonly List<string> _keys = [];

        public int Count => _keys.Count;

        public string KeyAt(int index) => _keys[index];

        public bool TryGet(string key, out string value)
        {
            if (_store.TryGetValue(key, out var found))
            {
                value = found;
                return true;
            }

            value = string.Empty;
            return false;
        }

        public void Put(string key, string value) => Store(key, value);

        public bool Remove(string key)
        {
            if (!_store.Remove(key))
                return false;

            _keys.Remove(key);
            return true;
        }

        public void RemoveAll()
        {
            _store.Clear();
            _keys.Clear();
        }

        /// <inheritdoc />
        public bool TryGetNamed(string name, out JsValue value)
        {
            if (!InterfaceMembers.Contains(name) && _store.TryGetValue(name, out var stored))
            {
                value = JsValue.String(stored);
                return true;
            }

            value = JsValue.Missing;
            return false;
        }

        /// <summary>
        /// None. <c>Storage</c> has named property getters and setters and no indexed ones, so an
        /// area supplies no elements — see the third paragraph of the class remarks for the spelling
        /// that costs.
        /// </summary>
        public bool TryGetIndex(uint index, out JsValue value)
        {
            value = JsValue.Missing;
            return false;
        }

        /// <inheritdoc />
        public uint IndexedLength => 0;

        /// <inheritdoc />
        public bool TrySetNamed(string name, JsValue value)
        {
            // Declining leaves the ordinary assignment to happen, which is what replaces the member
            // rather than storing an item under its name — the behaviour a browser has for a
            // different reason and the one pages depend on either way.
            if (InterfaceMembers.Contains(name))
                return false;

            // The realm's ToString and not the handle's: an area holds strings and nothing else,
            // which is why `localStorage.count += 1` concatenates in a browser, and an object
            // assigned to a key runs its own toString to get there.
            Store(name, value.IsMissing ? "undefined" : realm.ToJsString(value));
            return true;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> SupportedNames =>
            _keys.Where(key => !InterfaceMembers.Contains(key)).ToArray();

        /// <inheritdoc />
        /// <remarks>
        /// The reason this file could not become an <see cref="IJsExotic"/> until the contract had a
        /// delete hook: without it the property would go and the item would stay, so <c>getItem</c>
        /// would keep answering for something the page had deleted.
        /// </remarks>
        public bool TryDeleteNamed(string name) => !InterfaceMembers.Contains(name) && Remove(name);

        private void Store(string key, string value)
        {
            if (!_store.ContainsKey(key))
                _keys.Add(key);

            _store[key] = value;
        }
    }
}
