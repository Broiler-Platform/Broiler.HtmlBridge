using Broiler.HtmlBridge.Jseal;

// Engine-typed for one thing: StorageObject completes its own property lookup and its own deletion,
// which is a JSObject override. See the last paragraph of the class remarks.
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;

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
/// <b>This is the one object that completes its own property lookup and has not become an
/// <see cref="IJsExotic"/>, and the reason is a gap in that contract rather than a missing realm.</b>
/// The realm arrives now — <c>DomBridge/Registration/Window.cs</c> passes it — so every member below
/// is minted through it and this file names an engine type for the backing object alone.
/// </para>
/// <para>
/// What the realm does not fix is the reason the object cannot move. <c>Storage</c> is the only one of
/// the six lookup-completing objects whose behaviour includes a <em>deletion</em>: the override on
/// <see cref="StorageObject"/> takes <c>delete localStorage.foo</c> out of the backing map, so
/// <c>getItem</c> stops answering for it and <c>length</c> and <c>key(n)</c> stop counting it.
/// <see cref="IJsExotic"/> declares hooks for a named read, an indexed read and a named write,
/// and none for a delete — so converting as the contract stands would leave the ordinary property
/// deleted and the item still in the store, which is a wrong answer rather than a missing feature.
/// The contract needs a delete hook before this object can move; reported rather than worked around.
/// </para>
/// </remarks>
internal static class WebStorageBinding
{
    /// <summary>
    /// The <c>Storage</c> interface members. A page's key by the same name must not overwrite the
    /// method — see <see cref="StorageObject.SetValue"/>.
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
        var storage = new StorageObject();
        var area = Runtime.JsInterop.FromEngineObject(storage);

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

    private static JsValue GetItem(StorageObject storage, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        return storage.TryGet(call.Realm.ToJsString(call[0]), out var val) ? JsValue.String(val) : JsValue.Null;
    }

    private static JsValue SetItem(StorageObject storage, in JsCall call)
    {
        if (call.Length >= 2)
            storage.Put(call.Realm.ToJsString(call[0]), call.Realm.ToJsString(call[1]));

        return JsValue.Undefined;
    }

    private static JsValue RemoveItem(StorageObject storage, in JsCall call)
    {
        if (call.Length > 0)
            storage.Remove(call.Realm.ToJsString(call[0]));

        return JsValue.Undefined;
    }

    private static JsValue Clear(StorageObject storage, in JsCall _)
    {
        storage.RemoveAll();
        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>key(n)</c> — the <c>n</c>th key in insertion order, or <c>null</c> past the end. Paired
    /// with <c>length</c> it is how a page enumerates an area it did not write itself; MediaWiki's
    /// <c>ext.centralNotice</c> key-value store sweeps its own keys exactly that way.
    /// </summary>
    private static JsValue Key(StorageObject storage, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;

        var index = call.Realm.ToNumber(call[0]);
        if (double.IsNaN(index) || index < 0 || index >= storage.Count)
            return JsValue.Null;

        return JsValue.String(storage.KeyAt((int)index));
    }

    /// <summary>
    /// A storage area: the ordered key/value map, plus the property mirror that makes
    /// <c>storage.foo</c> and <c>storage["foo"]</c> address the same item as
    /// <c>getItem</c>/<c>setItem</c> do.
    /// </summary>
    /// <remarks>
    /// Storage is a legacy platform object with named property getters and setters (HTML §12.2.2),
    /// so the two spellings are one item in a browser and pages use them interchangeably. The
    /// mirror runs both ways: an item written with <c>setItem</c> is defined as an own property,
    /// and a property a page assigns directly is written into the store — otherwise
    /// <c>storage.foo = 1</c> followed by <c>getItem('foo')</c> answers <c>null</c>, and neither
    /// <c>length</c> nor <c>key()</c> would count it.
    /// </remarks>
    private sealed class StorageObject : JSObject
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

        public void Put(string key, string value)
        {
            Store(key, value);

            // A key named like an interface member would replace the method with a string, leaving
            // the area without the very method the page is about to call. A browser gets away with
            // it because its methods live on the prototype and the named property merely shadows
            // them per-object; here they are own properties, so the item stays readable through
            // getItem/key/length and only the property mirror is skipped.
            if (!InterfaceMembers.Contains(key))
                base.SetValue((KeyString)key, new JSString(value), this, false);
        }

        public bool Remove(string key)
        {
            if (!_store.Remove(key))
                return false;

            _keys.Remove(key);
            if (!InterfaceMembers.Contains(key))
                base.Delete((KeyString)key);

            return true;
        }

        public void RemoveAll()
        {
            foreach (var key in _keys)
            {
                if (!InterfaceMembers.Contains(key))
                    base.Delete((KeyString)key);
            }

            _store.Clear();
            _keys.Clear();
        }

        protected override bool SetValue(KeyString name, JSValue value, JSValue receiver, bool throwError = true)
        {
            var key = name.ToString();
            if (!InterfaceMembers.Contains(key))
                Store(key, value?.ToString() ?? "undefined");

            return base.SetValue(name, value, receiver, throwError);
        }

        public override JSValue Delete(in KeyString key)
        {
            var name = key.ToString();
            if (_store.Remove(name))
                _keys.Remove(name);

            return base.Delete(in key);
        }

        private void Store(string key, string value)
        {
            if (!_store.ContainsKey(key))
                _keys.Add(key);

            _store[key] = value;
        }
    }
}
