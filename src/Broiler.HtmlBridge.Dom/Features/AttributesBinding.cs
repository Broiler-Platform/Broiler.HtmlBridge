using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The attributes feature binding module (HtmlBridge complexity-reduction roadmap Phase 3, P3.12). It
/// co-locates the DOM attribute object model — the <c>element.attributes</c> <c>NamedNodeMap</c> and
/// its <c>Attr</c> nodes — together with the attribute write path
/// (<c>setAttribute</c>/<c>removeAttribute</c> and their <c>NS</c> variants), which applies the change
/// to the canonical attribute set and coordinates the cross-cutting side effects (inline style, inline
/// event handlers, style invalidation, mutation records) through the narrow <see cref="IAttributesHost"/>
/// contract. The element's own <c>getAttribute</c>/<c>setAttribute</c>/… methods (registered among the
/// other element members in the bridge) delegate their write and Attr-node construction here. The
/// low-level, engine-neutral attribute scans (<c>TryGetAttribute</c>/<c>SetAttr</c>/<c>RemoveAttr</c>/
/// <c>AttributeNames</c>/<c>TryGetNsAttribute</c>) stay shared static helpers on <c>DomBridge</c> and
/// are called qualified (Phase 4 promotes them to Broiler.Dom).
/// </summary>
/// <remarks>
/// <para>
/// <b>The module is JSEAL throughout — the <c>Attr</c> object model, the live <c>NamedNodeMap</c> and
/// its six element-dependent operations, the write path and the element operations alike.</b> The two
/// installers that used to hand it an engine argument frame both mint through the realm now:
/// <c>DomBridge/JsObjects.cs</c> for <c>removeAttributeNodeNS</c>, the one attribute member that stays
/// each wrapper's own property, and <see cref="DomCollectionBinding"/> for the map operations, whose
/// contract is <see cref="JsNativeFunction"/> since its own migration.
/// </para>
/// <para>
/// <b>One engine reference remains and it is not this module's to remove.</b>
/// <see cref="BuildStandaloneAttrNode"/> answers an engine object because
/// <c>DomBridge/DomBridge.DocumentFactoryHost.cs</c>, which reaches it for
/// <c>document.createAttribute</c>, still holds one. It converts with
/// <see cref="Runtime.JsInterop"/> or not at all: it carries an object across without converting it —
/// the handle holds the engine's own object — and cannot carry a primitive.
/// <para>
/// The second used to be the <c>InvalidCharacterError</c> that <c>setAttribute</c> and
/// <c>toggleAttribute</c> must throw, which the bridge's name validator minted from a script context
/// this contract had to carry. The validator takes a realm now, so the contract member is gone and
/// the three call sites use <c>call.Realm</c> — the realm the call arrived through.
/// </para>
/// </para>
/// </remarks>
internal sealed class AttributesBinding(IAttributesHost host)
{
    private readonly IAttributesHost _host = host;

    // -------- element.attributes NamedNodeMap --------

    /// <summary>
    /// <c>element.attributes</c> — a live <c>NamedNodeMap</c> (DOM §4.9.1), built once per element
    /// and cached, so <c>el.attributes === el.attributes</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to be a fresh plain object per read, with the same three faults the document
    /// collections had before they were moved onto <see cref="DomCollectionBinding"/>: no interface
    /// (<c>constructor.name</c> was <c>"Object"</c> and the bare name <c>NamedNodeMap</c> was a
    /// <c>ReferenceError</c>, which aborts the script that named it), no identity, and no named
    /// access — <c>el.attributes.id</c> was <c>undefined</c> where DOM §4.9.1 makes a qualified name
    /// a supported property name.
    /// </para>
    /// <para>
    /// The fourth fault was the dangerous one, because it made the idiomatic loop throw rather than
    /// answer wrongly. <c>length</c> was a live getter while the indices were materialized once at
    /// build time, so a map held across a <c>setAttribute</c> reported the new count with nothing at
    /// the new index: <c>for (var i = 0; i &lt; m.length; i++) m[i].name</c> read <c>undefined.name</c>
    /// and threw. Both halves are live now, from the same contents function.
    /// </para>
    /// <para>
    /// The caller, the middle and the answer are all JSEAL now: <see cref="DomCollectionBinding"/>
    /// mints the collection in this host's realm, the contents function answers with handles, and the
    /// six operations receive a <see cref="JsCall"/>. Nothing on this path converts.
    /// </para>
    /// </remarks>
    internal JsValue BuildNamedNodeMap(DomElement element, JsValue ownerObj)
    {
        if (_namedNodeMaps.TryGetValue(element, out var cached))
            return cached.Value;

        var owner = ownerObj;
        var map = DomCollectionBinding.NamedNodeMap(
            _host.Realm,
            () =>
            {
                var attributes = new List<JsValue>();
                foreach (var name in DomBridge.AttributeNames(element))
                    attributes.Add(AttrNodeFor(element, name, owner));
                return attributes;
            },
            name => DomBridge.HasAttr(element, name) ? AttrNodeFor(element, name, owner) : null,
            new DomCollectionBinding.NamedNodeMapOperations
            {
                GetNamedItem = (in call) => GetNamedItem(element, owner, in call),
                GetNamedItemNS = (in call) => GetNamedItemNS(element, owner, in call),
                SetNamedItem = (in call) => SetNamedItem(element, owner, in call),
                SetNamedItemNS = (in call) => SetNamedItemNS(element, owner, in call),
                RemoveNamedItem = (in call) => RemoveNamedItem(element, owner, in call),
                RemoveNamedItemNS = (in call) => RemoveNamedItemNS(element, owner, in call),
            });

        _namedNodeMaps.Add(element, new System.Runtime.CompilerServices.StrongBox<JsValue>(map));
        return map;
    }

    /// <summary>One live <c>NamedNodeMap</c> per element, and one <c>Attr</c> per attribute on it.</summary>
    /// <remarks>
    /// Weak tables, so neither cache keeps an element alive after the page has dropped it. The
    /// <c>Attr</c> cache is what makes the map's own identity meaningful: without it every read of
    /// the live contents would mint fresh attribute nodes, so <c>el.attributes[0] ===
    /// el.attributes[0]</c> would stay <see langword="false"/> however well the map itself were
    /// cached — and a browser answers <see langword="true"/> across every access path, the index,
    /// the qualified name, <c>getNamedItem</c> and <c>getAttributeNode</c> alike.
    /// <para>
    /// The map cache boxes its handle because a <see cref="ConditionalWeakTable{TKey,TValue}"/> value
    /// must be a reference type and a <see cref="JsValue"/> is a struct — the same reason the
    /// <c>Attr</c> cache below stores a dictionary rather than a value. The box is the only thing that
    /// changed when the cache stopped holding the engine's own object: a handle carries that very
    /// object, so a cached entry is still <c>===</c> the one the first read produced.
    /// </para>
    /// </remarks>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<
        DomElement, System.Runtime.CompilerServices.StrongBox<JsValue>> _namedNodeMaps = new();

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DomElement, Dictionary<string, JsValue>> _attrNodes = new();

    /// <summary>
    /// The single <c>Attr</c> wrapper for <paramref name="name"/> on <paramref name="element"/>,
    /// minted once and reused while the attribute exists.
    /// </summary>
    internal JsValue AttrNodeFor(DomElement element, string name, JsValue ownerObj)
    {
        var byName = _attrNodes.GetOrCreateValue(element);
        if (byName.TryGetValue(name, out var cached))
            return cached;

        var attr = BuildAttrNodeCore(element, name, ownerObj);
        byName[name] = attr;
        return attr;
    }

    /// <summary>
    /// Detaches the <c>Attr</c> wrapper for a removed attribute: it keeps the value it had and its
    /// <c>ownerElement</c> becomes <see langword="null"/>, and it leaves the cache so re-adding the
    /// attribute mints a new node rather than reviving the old one. Measured — a browser answers
    /// exactly that, and the difference is observable: the old node and the new one report the old
    /// and the new value respectively.
    /// </summary>
    /// <remarks>
    /// The three redefinitions replace the live accessors with plain values, which is what freezes
    /// the node: they are installed with the same enumerable/configurable flags the accessors had,
    /// and re-defining a configurable property is what lets a value take an accessor's place.
    /// </remarks>
    private void DetachAttrNode(DomElement element, string name)
    {
        if (!_attrNodes.TryGetValue(element, out var byName) || !byName.TryGetValue(name, out var attr))
            return;

        byName.Remove(name);
        DomBridge.TryGetAttribute(element, name, out var lastValue);
        var realm = _host.Realm;
        realm.DefineValue(attr, "ownerElement", JsValue.Null);
        realm.DefineValue(attr, "value", JsValue.String(lastValue ?? string.Empty));
        realm.DefineValue(attr, "nodeValue", JsValue.String(lastValue ?? string.Empty));
    }

    /// <summary>
    /// The <c>Attr</c> a <c>setAttributeNode</c>-family call must hand back, applying DOM §4.9.2's
    /// distinction between replacing an attribute with a <em>different</em> node and re-setting the
    /// one already on the element.
    /// </summary>
    /// <remarks>
    /// Re-setting the same node returns that node, still attached and reading the element's current
    /// value — so <c>attr.value = 'new'; el.setAttributeNode(attr)</c> hands back <c>attr</c> with
    /// <c>'new'</c>. Replacing it with a different node detaches the old one instead: it keeps the
    /// value it had and its <c>ownerElement</c> becomes <see langword="null"/>. Both were measured;
    /// the first is what the previous snapshot model got wrong, because it returned an object frozen
    /// at the old value where a browser returns the live node.
    /// <para>
    /// "The same node" is <c>===</c>, which <see cref="JsValue"/>'s <c>==</c> is: for two object
    /// handles it is the reference test this used to spell out, because a handle carries the
    /// engine's own object rather than a copy of it.
    /// </para>
    /// </remarks>
    private JsValue ReplacedAttrNode(DomElement element, string name, JsValue incoming, JsValue ownerObj)
    {
        if (!DomBridge.TryGetAttribute(element, name, out _))
            return JsValue.Null;

        var existing = AttrNodeFor(element, name, ownerObj);
        if (existing == incoming)
            return existing;

        DetachAttrNode(element, name);
        return existing;
    }

    // -------- NamedNodeMap operations --------
    //
    // Every argument read is the realm's ECMAScript conversion rather than the handle's rendering,
    // because that is what the engine frame these bodies used to take was performing: passing an object
    // with its own toString to getNamedItem has always run it.

    private JsValue GetNamedItem(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        var name = call.Realm.ToJsString(call[0]);
        if (!DomBridge.TryGetAttribute(element, name, out var val))
            return JsValue.Null;
        return BuildAttrNode(name, val, element, ownerObj);
    }

    private JsValue GetNamedItemNS(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Null;
        var ns = call[0].IsNullish ? null : call.Realm.ToJsString(call[0]);
        var localName = call.Realm.ToJsString(call[1]);
        if (!DomBridge.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
            return JsValue.Null;
        return BuildAttrNode(qName, val, element, ownerObj);
    }

    private JsValue SetNamedItem(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        if (!call[0].IsObject)
            return JsValue.Null;
        var incoming = call[0];
        var name = GetAttrNodeName(incoming);
        if (string.IsNullOrEmpty(name))
            return JsValue.Null;
        var value = AttrNodeValue(incoming);
        var old = ReplacedAttrNode(element, name, incoming, ownerObj);
        SetAttributeLikeSetAttribute(element, name, value);
        return old;
    }

    /// <remarks>
    /// Deliberately not sharing a body with <see cref="SetAttributeNodeNS"/>, which runs the same DOM
    /// §4.9.2 steps in a different order: this one reads the incoming node's <c>value</c> before it
    /// resolves the attribute it replaces, and the element's version reads it after. Both readings can
    /// run a getter the page wrote, so the order is observable and the difference is preserved rather
    /// than collapsed. See the note on <see cref="SetAttributeNodeNS"/>.
    /// </remarks>
    private JsValue SetNamedItemNS(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.Null;
        var incoming = call[0];
        var name = GetAttrNodeName(incoming);
        var localName = GetAttrNodeLocalName(incoming);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(localName))
            return JsValue.Null;
        var ns = GetAttrNodeNamespace(incoming);
        var value = AttrNodeValue(incoming);
        var old = DomBridge.TryGetNsAttribute(element, ns, localName, out var oldQName, out _)
            ? ReplacedAttrNode(element, oldQName, incoming, ownerObj)
            : JsValue.Null;
        SetAttributeLikeSetAttributeNS(element, ns, name, localName, value);
        return old;
    }

    private JsValue RemoveNamedItem(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        var name = call.Realm.ToJsString(call[0]);
        if (!DomBridge.TryGetAttribute(element, name, out var val))
            return JsValue.Null;
        var removed = BuildAttrNode(name, val, element, ownerObj);
        RemoveAttributeLikeRemoveAttribute(element, name);
        return removed;
    }

    private JsValue RemoveNamedItemNS(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Null;
        var ns = call[0].IsNullish ? null : call.Realm.ToJsString(call[0]);
        var localName = call.Realm.ToJsString(call[1]);
        if (!DomBridge.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
            return JsValue.Null;
        var removed = BuildAttrNode(qName, val, element, ownerObj);
        RemoveAttributeLikeRemoveAttributeNS(element, ns, localName);
        return removed;
    }

    // -------- Attr node construction --------

    /// <summary>Builds an <c>Attr</c>-like object with name, value, specified, ownerElement,
    /// nodeType, nodeName, localName, prefix and namespaceURI.</summary>
    internal JsValue BuildAttrNode(string name, string value, DomElement element, JsValue ownerObj) =>
        AttrNodeFor(element, name, ownerObj);

    /// <summary>
    /// A parentless <c>Attr</c> (<c>document.createAttribute</c>). Engine-typed because its caller,
    /// <c>DomBridge.DocumentFactoryHost.cs</c>, is not migrated.
    /// </summary>
    internal JavaScript.Runtime.JSObject BuildStandaloneAttrNode(string qualifiedName, string? namespaceUri) =>
        Runtime.JsInterop.ToEngineObject(
            BuildAttrNodeShell(qualifiedName, JsValue.Null, namespaceUri, null, JsValue.String(string.Empty), null));

    /// <summary>
    /// The <c>Attr</c> wrapper for an attribute that is <em>on</em> an element, so its <c>value</c>
    /// reads through to the element and writing it writes back.
    /// </summary>
    /// <remarks>
    /// A live accessor rather than the captured string this used to store. With the wrapper now
    /// cached per attribute rather than minted per read, a snapshot would be worse than it was:
    /// the one surviving object would go on reporting whatever the value happened to be when it was
    /// first asked for. A browser's <c>value</c> tracks the element in both directions —
    /// <c>attr.value = 'x'</c> is another spelling of <c>setAttribute</c> — and both directions are
    /// pinned.
    /// </remarks>
    private JsValue BuildAttrNodeCore(DomElement element, string name, JsValue ownerObj)
    {
        var namespaceUri = TryGetAttachedAttrNamespace(element, name, out var ns, out var localName)
            ? ns
            : null;

        JsValue ReadValue() =>
            JsValue.String(DomBridge.TryGetAttribute(element, name, out var current) ? current : string.Empty);

        JsValue WriteValue(in JsCall call)
        {
            // The realm's ToString, not the handle's: assigning an object to attr.value runs that
            // object's own toString, which is the coercion a page observes here.
            SetAttributeLikeSetAttribute(
                element, name, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
            return JsValue.Undefined;
        }

        return BuildAttrNodeShell(
            name,
            ownerObj,
            namespaceUri,
            localName,
            liveValue: null,
            (ReadValue, WriteValue));
    }

    /// <summary>The members every <c>Attr</c> carries, attached or standalone.</summary>
    private JsValue BuildAttrNodeShell(
        string name,
        JsValue ownerElement,
        string? namespaceUri,
        string? explicitLocalName,
        JsValue? liveValue,
        (Func<JsValue> Read, JsNativeFunction Write)? accessor)
    {
        var realm = _host.Realm;
        var attr = realm.NewObject();
        // An attribute is not a DomNode in the canonical DOM, so its wrapper never reaches the node
        // choke point where every other wrapper is linked to its interface — hence the explicit
        // call. Without it an Attr reported constructor.name of 'Object' like the rest used to.
        _host.LinkToInterface(attr, "Attr");
        var colonIdx = name.IndexOf(':');
        var localName = explicitLocalName ?? (colonIdx >= 0 ? name[(colonIdx + 1)..] : name);
        var prefix = colonIdx >= 0 ? name[..colonIdx] : null;

        realm.DefineValue(attr, "name", JsValue.String(name));
        if (accessor is { } live)
        {
            // The realm mints and names both halves; a getter carries length 0 and a setter length 1,
            // which is what a browser's accessor pair reports.
            realm.DefineAccessor(attr, "value", (in _) => live.Read(), live.Write);
            realm.DefineAccessor(attr, "nodeValue", (in _) => live.Read(), live.Write);
        }
        else
        {
            realm.DefineValue(attr, "value", liveValue ?? JsValue.String(string.Empty));
            realm.DefineValue(attr, "nodeValue", liveValue ?? JsValue.String(string.Empty));
        }

        realm.DefineValue(attr, "specified", JsValue.True);
        realm.DefineValue(attr, "ownerElement", ownerElement);
        realm.DefineValue(attr, "nodeType", JsValue.Number(2));
        realm.DefineValue(attr, "nodeName", JsValue.String(name));
        realm.DefineValue(attr, "localName", JsValue.String(localName));
        realm.DefineValue(attr, "prefix", prefix != null ? JsValue.String(prefix) : JsValue.Null);
        realm.DefineValue(attr, "namespaceURI", namespaceUri != null ? JsValue.String(namespaceUri) : JsValue.Null);

        return attr;
    }

    private static bool TryGetAttachedAttrNamespace(DomElement element, string qualifiedName, out string? namespaceUri, out string localName)
    {
        // Match a genuinely namespaced attribute (non-null namespace) by qualified name —
        // the set NsAttrMap used to track. No-namespace attributes are skipped so the
        // colon-split fallback below governs their local name, exactly as before: a
        // prefixed qualified name can only carry a namespace, so this never drops one.
        foreach (var attribute in element.Attributes.Values)
        {
            if (attribute.NamespaceUri is null || !string.Equals(attribute.QualifiedName, qualifiedName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            namespaceUri = attribute.NamespaceUri;
            localName = attribute.LocalName;
            return true;
        }

        namespaceUri = null;
        var colonIdx = qualifiedName.IndexOf(':');
        localName = colonIdx >= 0 ? qualifiedName[(colonIdx + 1)..] : qualifiedName;
        return false;
    }

    /// <summary>
    /// The <c>value</c> a <c>setAttributeNode</c>-family call is asked to write, coerced the way the
    /// page would see it: reading the member may run a getter, and rendering it may run a
    /// <c>toString</c>.
    /// </summary>
    private string AttrNodeValue(JsValue attrObj)
    {
        var realm = _host.Realm;
        return realm.ToJsString(realm.GetProperty(attrObj, "value"));
    }

    internal string GetAttrNodeName(JsValue attrObj)
    {
        var realm = _host.Realm;
        var nameValue = realm.GetProperty(attrObj, "name");
        if (!nameValue.IsNullish)
            return realm.ToJsString(nameValue);

        var nodeNameValue = realm.GetProperty(attrObj, "nodeName");
        return !nodeNameValue.IsNullish ? realm.ToJsString(nodeNameValue) : string.Empty;
    }

    internal string GetAttrNodeLocalName(JsValue attrObj)
    {
        var realm = _host.Realm;
        var localNameValue = realm.GetProperty(attrObj, "localName");
        if (!localNameValue.IsNullish)
            return realm.ToJsString(localNameValue);

        var name = GetAttrNodeName(attrObj);
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var colonIdx = name.IndexOf(':');
        return colonIdx >= 0 ? name[(colonIdx + 1)..] : name;
    }

    internal string? GetAttrNodeNamespace(JsValue attrObj)
    {
        var realm = _host.Realm;
        var namespaceValue = realm.GetProperty(attrObj, "namespaceURI");
        return !namespaceValue.IsNullish ? realm.ToJsString(namespaceValue) : null;
    }

    // -------- Attribute write path (setAttribute / removeAttribute + NS variants) --------

    internal void SetAttributeLikeSetAttribute(DomElement element, string attrName, string attrVal)
    {
        DomBridge.TryGetAttribute(element, attrName, out var previousAttrVal);
        DomBridge.SetAttr(element, attrName, attrVal);
        if (string.Equals(attrName, "id", StringComparison.OrdinalIgnoreCase))
            element.Id = attrVal;
        else if (string.Equals(attrName, "class", StringComparison.OrdinalIgnoreCase))
            element.ClassName = attrVal;
        else if (string.Equals(attrName, "style", StringComparison.OrdinalIgnoreCase))
        {
            _host.ApplyStyleAttribute(element, attrVal);
        }
        else if (attrName.Length > 2 && attrName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            _host.CompileInlineEventAttribute(element, attrName, attrVal);
        }

        if (!string.Equals(attrName, "style", StringComparison.OrdinalIgnoreCase))
            _host.InvalidateStyleScope(element);

        if (!string.Equals(previousAttrVal, attrVal, StringComparison.Ordinal))
            _host.NotifyAttributeMutationObservers(element, attrName, previousAttrVal);
    }

    internal void RemoveAttributeLikeRemoveAttribute(DomElement element, string attrName)
    {
        DomBridge.TryGetAttribute(element, attrName, out var previousAttrVal);
        // Before the removal, so the wrapper can keep the value it had.
        DetachAttrNode(element, attrName);
        var removed = DomBridge.RemoveAttr(element, attrName);
        if (string.Equals(attrName, "id", StringComparison.OrdinalIgnoreCase))
            element.Id = null;
        else if (string.Equals(attrName, "class", StringComparison.OrdinalIgnoreCase))
            element.ClassName = null;

        _host.InvalidateStyleScope(element);
        if (removed)
            _host.NotifyAttributeMutationObservers(element, attrName, previousAttrVal);
    }

    internal void SetAttributeLikeSetAttributeNS(DomElement element, string? namespaceUri, string attrName, string localName, string attrVal)
    {
        string? previousAttrVal = null;
        if (DomBridge.TryGetNsAttribute(element, namespaceUri, localName, out var previousQualifiedName, out var existingAttrVal))
        {
            previousAttrVal = existingAttrVal;
            // A prefix change keeps the same (namespace, localName) canonical key, so the
            // SetAttributeNS below replaces the old-prefix attribute in place. The explicit
            // remove keeps the canonical mutation-record sequence identical to the shadow-map era.
            if (!string.Equals(previousQualifiedName, attrName, StringComparison.OrdinalIgnoreCase))
                DomBridge.RemoveAttr(element, previousQualifiedName);
        }
        else
        {
            DomBridge.TryGetAttribute(element, attrName, out previousAttrVal);
        }

        element.SetAttributeNS(namespaceUri, attrName, attrVal);
        if (string.Equals(attrName, "id", StringComparison.OrdinalIgnoreCase))
            element.Id = attrVal;
        else if (string.Equals(attrName, "class", StringComparison.OrdinalIgnoreCase))
            element.ClassName = attrVal;
        else if (string.Equals(attrName, "style", StringComparison.OrdinalIgnoreCase))
        {
            _host.ApplyStyleAttribute(element, attrVal);
        }
        else if (attrName.Length > 2 && attrName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
        {
            _host.CompileInlineEventAttribute(element, attrName, attrVal);
        }

        if (!string.Equals(attrName, "style", StringComparison.OrdinalIgnoreCase))
            _host.InvalidateStyleScope(element);

        if (!string.Equals(previousAttrVal, attrVal, StringComparison.Ordinal))
            _host.NotifyAttributeMutationObservers(element, attrName, previousAttrVal);
    }

    internal void RemoveAttributeLikeRemoveAttributeNS(DomElement element, string? namespaceUri, string localName)
    {
        if (!DomBridge.TryGetNsAttribute(element, namespaceUri, localName, out var attrName, out var previousAttrVal))
            return;

        DetachAttrNode(element, attrName);
        var removed = DomBridge.RemoveAttr(element, attrName);
        if (string.Equals(attrName, "id", StringComparison.OrdinalIgnoreCase))
            element.Id = null;
        else if (string.Equals(attrName, "class", StringComparison.OrdinalIgnoreCase))
            element.ClassName = null;

        _host.InvalidateStyleScope(element);
        if (removed)
            _host.NotifyAttributeMutationObservers(element, attrName, previousAttrVal);
    }

    // -------- Element attribute methods (element.getAttribute / setAttribute / … , registered on
    // Element.prototype by DomBridge/ElementInterface.cs; they delegate the write and Attr-node
    // construction into this module).
    //
    // Every argument read is the realm's ECMAScript conversion rather than the handle's rendering,
    // because that is what the engine frame these bodies used to take was performing:
    // `el.getAttribute({toString(){…}})` has always run the object's own toString here. --------

    internal JsValue GetAttribute(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        var name = call.Realm.ToJsString(call[0]);
        return DomBridge.TryGetAttribute(element, name, out var val) ? JsValue.String(val) : JsValue.Null;
    }

    /// <summary>
    /// <c>element.setAttribute(name, value)</c> — DOM §4.9.1, which begins by requiring
    /// <paramref name="call" />'s name to match the XML <c>Name</c> production and throwing
    /// <c>InvalidCharacterError</c> when it does not.
    /// </summary>
    /// <remarks>
    /// Every invalid name used to be written through silently, so <c>setAttribute('@click', …)</c>
    /// and <c>setAttribute('foo bar', …)</c> produced an attribute a browser refuses to create — and
    /// the one name that did fail, the empty string, threw a bare <c>Error</c> with no <c>name</c> or
    /// <c>code</c> for a caller to branch on rather than a <c>DOMException</c>.
    /// </remarks>
    internal JsValue SetAttribute(DomElement element, in JsCall call)
    {
        if (call.Length >= 2)
        {
            var name = call.Realm.ToJsString(call[0]);
            DomBridge.ValidateAttributeName(name, call.Realm);
            SetAttributeLikeSetAttribute(element, name, call.Realm.ToJsString(call[1]));
        }

        return JsValue.Undefined;
    }

    internal JsValue GetAttributeNode(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        var name = call.Realm.ToJsString(call[0]);
        return DomBridge.TryGetAttribute(element, name, out var val)
            ? BuildAttrNode(name, val, element, ownerObj)
            : JsValue.Null;
    }

    internal JsValue GetAttributeNodeNS(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Null;
        var ns = call[0].IsNullish ? null : call.Realm.ToJsString(call[0]);
        var localName = call.Realm.ToJsString(call[1]);
        if (!DomBridge.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
            return JsValue.Null;
        return BuildAttrNode(qName, val, element, ownerObj);
    }

    internal JsValue HasAttribute(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.False;
        return JsValue.Boolean(DomBridge.HasAttr(element, call.Realm.ToJsString(call[0])));
    }

    internal JsValue RemoveAttribute(DomElement element, in JsCall call)
    {
        if (call.Length > 0)
            RemoveAttributeLikeRemoveAttribute(element, call.Realm.ToJsString(call[0]));
        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>element.toggleAttribute(name, force)</c>. It validates the name the same way
    /// <see cref="SetAttribute"/> does — DOM §4.9.4 runs the identical check, and a browser throws
    /// from it, which was measured. <c>removeAttribute</c>, <c>hasAttribute</c> and
    /// <c>getAttribute</c> deliberately do not: they answer about a name rather than create one, and a
    /// browser accepts an invalid name from all three.
    /// </summary>
    internal JsValue ToggleAttribute(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.False;
        var attrName = call.Realm.ToJsString(call[0]);
        DomBridge.ValidateAttributeName(attrName, call.Realm);
        var hasAttribute = DomBridge.HasAttr(element, attrName);
        var forceSpecified = call.Length > 1 && !call[1].IsUndefined;
        var shouldHaveAttribute = forceSpecified ? call[1].AsBoolean : !hasAttribute;
        if (shouldHaveAttribute)
        {
            if (!hasAttribute)
                SetAttributeLikeSetAttribute(element, attrName, string.Empty);
            return JsValue.True;
        }

        if (hasAttribute)
            RemoveAttributeLikeRemoveAttribute(element, attrName);
        return JsValue.False;
    }

    internal JsValue SetAttributeNode(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.Null;
        var incoming = call[0];
        var name = GetAttrNodeName(incoming);
        if (string.IsNullOrEmpty(name))
            return JsValue.Null;
        var old = ReplacedAttrNode(element, name, incoming, ownerObj);
        SetAttributeLikeSetAttribute(element, name, AttrNodeValue(incoming));
        return old;
    }

    /// <remarks>
    /// <c>NamedNodeMap.setNamedItemNS</c> runs the same DOM §4.9.2 steps in a different order — it
    /// reads the incoming node's <c>value</c> before resolving the attribute it replaces, and this one
    /// reads it after. Reading <c>value</c> can run a getter the page wrote, so the two orders are
    /// observably different; the difference is pre-existing and is left as it is rather than being
    /// collapsed under a migration.
    /// </remarks>
    internal JsValue SetAttributeNodeNS(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.Null;
        var incoming = call[0];
        var name = GetAttrNodeName(incoming);
        var localName = GetAttrNodeLocalName(incoming);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(localName))
            return JsValue.Null;
        var ns = GetAttrNodeNamespace(incoming);
        var old = DomBridge.TryGetNsAttribute(element, ns, localName, out var oldQName, out _)
            ? ReplacedAttrNode(element, oldQName, incoming, ownerObj)
            : JsValue.Null;
        SetAttributeLikeSetAttributeNS(element, ns, name, localName, AttrNodeValue(incoming));
        return old;
    }

    internal JsValue RemoveAttributeNode(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.Null;
        var name = GetAttrNodeName(call[0]);
        if (string.IsNullOrEmpty(name) || !DomBridge.TryGetAttribute(element, name, out var val))
            return JsValue.Null;
        var removed = BuildAttrNode(name, val, element, ownerObj);
        RemoveAttributeLikeRemoveAttribute(element, name);
        return removed;
    }

    /// <summary>
    /// <c>element.removeAttributeNodeNS(attr)</c>, the one attribute member that stays each wrapper's
    /// own property rather than moving to <c>Element.prototype</c> — DOM §4.9 gives
    /// <c>removeAttributeNode</c> no namespace-qualified sibling, so no browser's prototype carries
    /// one. Installed by <c>DomBridge/JsObjects.cs</c>, which mints it through the realm like the rest.
    /// </summary>
    internal JsValue RemoveAttributeNodeNS(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.Null;
        return RemoveAttributeNodeNsCore(element, ownerObj, call[0]);
    }

    private JsValue RemoveAttributeNodeNsCore(DomElement element, JsValue ownerObj, JsValue incoming)
    {
        var localName = GetAttrNodeLocalName(incoming);
        if (string.IsNullOrEmpty(localName))
            return JsValue.Null;
        var ns = GetAttrNodeNamespace(incoming);
        if (!DomBridge.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
            return JsValue.Null;
        var removed = BuildAttrNode(qName, val, element, ownerObj);
        RemoveAttributeLikeRemoveAttributeNS(element, ns, localName);
        return removed;
    }

    /// <summary>
    /// <c>element.setAttributeNS(namespace, qualifiedName, value)</c> — DOM §4.9.2, whose first step
    /// is the "validate and extract" algorithm. That is the qualified-name rule
    /// <c>createElementNS</c> already used, so this reuses it rather than carrying a second reading:
    /// an invalid character is an <c>InvalidCharacterError</c> and a prefix without a namespace a
    /// <c>NamespaceError</c>.
    /// </summary>
    internal JsValue SetAttributeNS(DomElement element, in JsCall call)
    {
        if (call.Length >= 3)
        {
            var ns = call[0].IsNullish ? null : call.Realm.ToJsString(call[0]);
            var qName = call.Realm.ToJsString(call[1]);
            var val = call.Realm.ToJsString(call[2]);
            // The realm of the call rather than the host's, and the null check that used to
            // guard this went with it: a JsCall exists only because guest code is running, so the
            // realm it carries cannot be absent. What the old guard tolerated was a null script
            // CONTEXT on an unattached bridge, which this path could never reach.
            DomBridge.ValidateQualifiedName(qName, ns, call.Realm);
            var localName = qName.Contains(':') ? qName[(qName.IndexOf(':') + 1)..] : qName;
            SetAttributeLikeSetAttributeNS(element, ns, qName, localName, val);
        }

        return JsValue.Undefined;
    }

    internal JsValue GetAttributeNS(DomElement element, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Null;
        var ns = call[0].IsNullish ? null : call.Realm.ToJsString(call[0]);
        var localName = call.Realm.ToJsString(call[1]);
        var val = element.GetAttributeNS(ns, localName);
        return val is not null ? JsValue.String(val) : JsValue.Null;
    }

    internal JsValue RemoveAttributeNS(DomElement element, in JsCall call)
    {
        if (call.Length >= 2)
        {
            var ns = call[0].IsNullish ? null : call.Realm.ToJsString(call[0]);
            var localName = call.Realm.ToJsString(call[1]);
            RemoveAttributeLikeRemoveAttributeNS(element, ns, localName);
        }

        return JsValue.Undefined;
    }

    internal JsValue HasAttributeNS(DomElement element, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.False;
        var ns = call[0].IsNullish ? null : call.Realm.ToJsString(call[0]);
        var localName = call.Realm.ToJsString(call[1]);
        return JsValue.Boolean(element.GetAttributeNS(ns, localName) is not null);
    }

}
