using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
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
    public static JSObject BuildStorage()
    {
        var storage = new StorageObject();

        // Non-enumerable, as they are in a browser: there the members live on Storage.prototype and
        // only the stored keys are own properties, so `for (var k in storage)` and
        // `Object.keys(storage)` yield keys alone. Bridge objects carry their members directly
        // (see RegisterDomInterfaceConstructors), so hiding them from enumeration is what keeps a
        // page that iterates a storage area from finding four methods among its keys.
        storage.FastAddValue("getItem",
            new DomFunction((in a) => GetItem(storage, in a), "getItem", 1),
            JSPropertyAttributes.ConfigurableValue);

        storage.FastAddValue("setItem",
            new DomFunction((in a) => SetItem(storage, in a), "setItem", 2),
            JSPropertyAttributes.ConfigurableValue);

        storage.FastAddValue("removeItem",
            new DomFunction((in a) => RemoveItem(storage, in a), "removeItem", 1),
            JSPropertyAttributes.ConfigurableValue);

        storage.FastAddValue("clear",
            new DomFunction((in a) => Clear(storage, in a), "clear", 0),
            JSPropertyAttributes.ConfigurableValue);

        storage.FastAddValue("key",
            new DomFunction((in a) => Key(storage, in a), "key", 1),
            JSPropertyAttributes.ConfigurableValue);

        storage.FastAddProperty("length",
            new DomFunction((in _) => new JSNumber(storage.Count), "get length"),
            null,
            JSPropertyAttributes.ConfigurableProperty);

        return storage;
    }

    private static JSValue GetItem(StorageObject storage, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        return storage.TryGet(a[0].ToString(), out var val) ? new JSString(val) : JSNull.Value;
    }

    private static JSValue SetItem(StorageObject storage, in Arguments a)
    {
        if (a.Length >= 2)
            storage.Put(a[0].ToString(), a[1].ToString());

        return JSUndefined.Value;
    }

    private static JSValue RemoveItem(StorageObject storage, in Arguments a)
    {
        if (a.Length > 0)
            storage.Remove(a[0].ToString());

        return JSUndefined.Value;
    }

    private static JSValue Clear(StorageObject storage, in Arguments _)
    {
        storage.RemoveAll();
        return JSUndefined.Value;
    }

    /// <summary>
    /// <c>key(n)</c> — the <c>n</c>th key in insertion order, or <c>null</c> past the end. Paired
    /// with <c>length</c> it is how a page enumerates an area it did not write itself; MediaWiki's
    /// <c>ext.centralNotice</c> key-value store sweeps its own keys exactly that way.
    /// </summary>
    private static JSValue Key(StorageObject storage, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;

        var index = a[0].DoubleValue;
        if (double.IsNaN(index) || index < 0 || index >= storage.Count)
            return JSNull.Value;

        return new JSString(storage.KeyAt((int)index));
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
