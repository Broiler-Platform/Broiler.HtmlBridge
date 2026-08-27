using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
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
    /// </remarks>
    internal JSObject BuildNamedNodeMap(DomElement element, JSObject ownerObj)
    {
        if (_namedNodeMaps.TryGetValue(element, out var cached))
            return cached;

        var map = DomCollectionBinding.NamedNodeMap(
            _host.JsContext,
            () =>
            {
                var attributes = new List<JSValue>();
                foreach (var name in DomBridge.AttributeNames(element))
                    attributes.Add(AttrNodeFor(element, name, ownerObj));
                return attributes;
            },
            name => DomBridge.HasAttr(element, name) ? AttrNodeFor(element, name, ownerObj) : null,
            new DomCollectionBinding.NamedNodeMapOperations
            {
                GetNamedItem = a => GetNamedItem(element, ownerObj, in a),
                GetNamedItemNS = a => GetNamedItemNS(element, ownerObj, in a),
                SetNamedItem = a => SetNamedItem(element, ownerObj, in a),
                SetNamedItemNS = a => SetNamedItemNS(element, ownerObj, in a),
                RemoveNamedItem = a => RemoveNamedItem(element, ownerObj, in a),
                RemoveNamedItemNS = a => RemoveNamedItemNS(element, ownerObj, in a),
            });

        if (map is not JSObject instance)
            return new JSObject();

        _namedNodeMaps.Add(element, instance);
        return instance;
    }

    /// <summary>One live <c>NamedNodeMap</c> per element, and one <c>Attr</c> per attribute on it.</summary>
    /// <remarks>
    /// Weak tables, so neither cache keeps an element alive after the page has dropped it. The
    /// <c>Attr</c> cache is what makes the map's own identity meaningful: without it every read of
    /// the live contents would mint fresh attribute nodes, so <c>el.attributes[0] ===
    /// el.attributes[0]</c> would stay <see langword="false"/> however well the map itself were
    /// cached — and a browser answers <see langword="true"/> across every access path, the index,
    /// the qualified name, <c>getNamedItem</c> and <c>getAttributeNode</c> alike.
    /// </remarks>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DomElement, JSObject> _namedNodeMaps = new();

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DomElement, Dictionary<string, JSObject>> _attrNodes = new();

    /// <summary>
    /// The single <c>Attr</c> wrapper for <paramref name="name"/> on <paramref name="element"/>,
    /// minted once and reused while the attribute exists.
    /// </summary>
    internal JSObject AttrNodeFor(DomElement element, string name, JSValue ownerObj)
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
    private void DetachAttrNode(DomElement element, string name)
    {
        if (!_attrNodes.TryGetValue(element, out var byName) || !byName.TryGetValue(name, out var attr))
            return;

        byName.Remove(name);
        DomBridge.TryGetAttribute(element, name, out var lastValue);
        attr.FastAddValue("ownerElement", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        attr.FastAddValue("value", new JSString(lastValue ?? string.Empty), JSPropertyAttributes.EnumerableConfigurableValue);
        attr.FastAddValue("nodeValue", new JSString(lastValue ?? string.Empty), JSPropertyAttributes.EnumerableConfigurableValue);
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
    /// </remarks>
    private JSValue ReplacedAttrNode(DomElement element, string name, JSObject incoming, JSValue ownerObj)
    {
        if (!DomBridge.TryGetAttribute(element, name, out _))
            return JSNull.Value;

        var existing = AttrNodeFor(element, name, ownerObj);
        if (ReferenceEquals(existing, incoming))
            return existing;

        DetachAttrNode(element, name);
        return existing;
    }

    private JSValue GetNamedItem(DomElement element, JSObject ownerObj, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        var name = a[0].ToString();
        if (!DomBridge.TryGetAttribute(element, name, out var val))
            return JSNull.Value;
        return BuildAttrNode(name, val, element, ownerObj);
    }

    private JSValue GetNamedItemNS(DomElement element, JSObject ownerObj, in Arguments a)
    {
        if (a.Length < 2)
            return JSNull.Value;
        var ns = a[0].IsNull || a[0].IsUndefined ? null : a[0].ToString();
        var localName = a[1].ToString();
        if (!DomBridge.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
            return JSNull.Value;
        return BuildAttrNode(qName, val, element, ownerObj);
    }

    private JSValue SetNamedItem(DomElement element, JSObject ownerObj, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        if (a[0] is not JSObject attrObj)
            return JSNull.Value;
        var name = GetAttrNodeName(attrObj);
        if (string.IsNullOrEmpty(name))
            return JSNull.Value;
        var value = attrObj[(KeyString)"value"].ToString();
        var old = ReplacedAttrNode(element, name, attrObj, ownerObj);
        SetAttributeLikeSetAttribute(element, name, value);
        return old;
    }

    private JSValue SetNamedItemNS(DomElement element, JSObject ownerObj, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject attrObj)
            return JSNull.Value;
        var name = GetAttrNodeName(attrObj);
        var localName = GetAttrNodeLocalName(attrObj);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(localName))
            return JSNull.Value;
        var ns = GetAttrNodeNamespace(attrObj);
        var value = attrObj[(KeyString)"value"].ToString();
        var old = DomBridge.TryGetNsAttribute(element, ns, localName, out var oldQName, out _)
            ? ReplacedAttrNode(element, oldQName, attrObj, ownerObj)
            : JSNull.Value;
        SetAttributeLikeSetAttributeNS(element, ns, name, localName, value);
        return old;
    }

    private JSValue RemoveNamedItem(DomElement element, JSObject ownerObj, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        var name = a[0].ToString();
        if (!DomBridge.TryGetAttribute(element, name, out var val))
            return JSNull.Value;
        var removed = BuildAttrNode(name, val, element, ownerObj);
        RemoveAttributeLikeRemoveAttribute(element, name);
        return removed;
    }

    private JSValue RemoveNamedItemNS(DomElement element, JSObject ownerObj, in Arguments a)
    {
        if (a.Length < 2)
            return JSNull.Value;
        var ns = a[0].IsNull || a[0].IsUndefined ? null : a[0].ToString();
        var localName = a[1].ToString();
        if (!DomBridge.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
            return JSNull.Value;
        var removed = BuildAttrNode(qName, val, element, ownerObj);
        RemoveAttributeLikeRemoveAttributeNS(element, ns, localName);
        return removed;
    }

    private JSValue Item(DomElement element, JSObject ownerObj, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        var idx = (int)a[0].DoubleValue;
        var keys = DomBridge.AttributeNames(element).ToList();
        if (idx < 0 || idx >= keys.Count)
            return JSNull.Value;
        var name = keys[idx];
        return BuildAttrNode(name, DomBridge.GetAttr(element, name) ?? string.Empty, element, ownerObj);
    }

    private JSValue IndexedItem(DomElement element, int idx, JSObject ownerObj, in Arguments _)
    {
        var keys = DomBridge.AttributeNames(element).ToList();
        if (idx >= keys.Count)
            return JSUndefined.Value;
        var n = keys[idx];
        return BuildAttrNode(n, DomBridge.GetAttr(element, n) ?? string.Empty, element, ownerObj);
    }

    // -------- Attr node construction --------

    /// <summary>Builds an <c>Attr</c>-like JSObject with name, value, specified, ownerElement,
    /// nodeType, nodeName, localName, prefix and namespaceURI.</summary>
    internal JSObject BuildAttrNode(string name, string value, DomElement element, JSObject ownerObj) =>
        AttrNodeFor(element, name, ownerObj);

    internal JSObject BuildStandaloneAttrNode(string qualifiedName, string? namespaceUri) =>
        BuildAttrNodeShell(qualifiedName, JSNull.Value, namespaceUri, null, new JSString(string.Empty), null);

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
    private JSObject BuildAttrNodeCore(DomElement element, string name, JSValue ownerObj)
    {
        var namespaceUri = TryGetAttachedAttrNamespace(element, name, out var ns, out var localName)
            ? ns
            : null;

        JSValue ReadValue() =>
            new JSString(DomBridge.TryGetAttribute(element, name, out var current) ? current : string.Empty);

        JSValue WriteValue(in Arguments a)
        {
            SetAttributeLikeSetAttribute(element, name, a.Length > 0 ? a[0].ToString() : string.Empty);
            return JSUndefined.Value;
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
    private JSObject BuildAttrNodeShell(
        string name,
        JSValue ownerElement,
        string? namespaceUri,
        string? explicitLocalName,
        JSValue? liveValue,
        (Func<JSValue> Read, JSFunctionDelegate Write)? accessor)
    {
        var attr = new JSObject();
        // An attribute is not a DomNode in the canonical DOM, so its wrapper never reaches the node
        // choke point where every other wrapper is linked to its interface — hence the explicit
        // call. Without it an Attr reported constructor.name of 'Object' like the rest used to.
        _host.LinkToInterface(attr, "Attr");
        var colonIdx = name.IndexOf(':');
        var localName = explicitLocalName ?? (colonIdx >= 0 ? name[(colonIdx + 1)..] : name);
        var prefix = colonIdx >= 0 ? name[..colonIdx] : null;

        attr.FastAddValue("name", new JSString(name), JSPropertyAttributes.EnumerableConfigurableValue);
        if (accessor is { } live)
        {
            attr.FastAddProperty("value",
                new DomFunction((in _) => live.Read(), "get value"),
                new DomFunction(live.Write, "set value"),
                JSPropertyAttributes.EnumerableConfigurableProperty);
            attr.FastAddProperty("nodeValue",
                new DomFunction((in _) => live.Read(), "get nodeValue"),
                new DomFunction(live.Write, "set nodeValue"),
                JSPropertyAttributes.EnumerableConfigurableProperty);
        }
        else
        {
            attr.FastAddValue("value", liveValue ?? new JSString(string.Empty), JSPropertyAttributes.EnumerableConfigurableValue);
            attr.FastAddValue("nodeValue", liveValue ?? new JSString(string.Empty), JSPropertyAttributes.EnumerableConfigurableValue);
        }

        attr.FastAddValue("specified", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        attr.FastAddValue("ownerElement", ownerElement, JSPropertyAttributes.EnumerableConfigurableValue);
        attr.FastAddValue("nodeType", new JSNumber(2), JSPropertyAttributes.EnumerableConfigurableValue);
        attr.FastAddValue("nodeName", new JSString(name), JSPropertyAttributes.EnumerableConfigurableValue);
        attr.FastAddValue("localName", new JSString(localName), JSPropertyAttributes.EnumerableConfigurableValue);
        attr.FastAddValue("prefix", prefix != null ? new JSString(prefix) : JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        attr.FastAddValue("namespaceURI", namespaceUri != null ? new JSString(namespaceUri) : JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);

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

    internal string GetAttrNodeName(JSObject attrObj)
    {
        var nameValue = attrObj[(KeyString)"name"];
        if (nameValue != null && !nameValue.IsUndefined && !nameValue.IsNull)
            return nameValue.ToString();

        var nodeNameValue = attrObj[(KeyString)"nodeName"];
        return nodeNameValue != null && !nodeNameValue.IsUndefined && !nodeNameValue.IsNull
            ? nodeNameValue.ToString()
            : string.Empty;
    }

    internal string GetAttrNodeLocalName(JSObject attrObj)
    {
        var localNameValue = attrObj[(KeyString)"localName"];
        if (localNameValue != null && !localNameValue.IsUndefined && !localNameValue.IsNull)
            return localNameValue.ToString();

        var name = GetAttrNodeName(attrObj);
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var colonIdx = name.IndexOf(':');
        return colonIdx >= 0 ? name[(colonIdx + 1)..] : name;
    }

    internal string? GetAttrNodeNamespace(JSObject attrObj)
    {
        var namespaceValue = attrObj[(KeyString)"namespaceURI"];
        return namespaceValue != null && !namespaceValue.IsUndefined && !namespaceValue.IsNull
            ? namespaceValue.ToString()
            : null;
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

    // -------- Element attribute methods (element.getAttribute / setAttribute / … , registered on the
    // element wrapper; they delegate the write and Attr-node construction into this module) --------

    internal JSValue GetAttribute(DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        var name = a[0].ToString();
        return DomBridge.TryGetAttribute(element, name, out var val) ? new JSString(val) : JSNull.Value;
    }

    /// <summary>
    /// <c>element.setAttribute(name, value)</c> — DOM §4.9.1, which begins by requiring
    /// <paramref name="a" />'s name to match the XML <c>Name</c> production and throwing
    /// <c>InvalidCharacterError</c> when it does not.
    /// </summary>
    /// <remarks>
    /// Every invalid name used to be written through silently, so <c>setAttribute('@click', …)</c>
    /// and <c>setAttribute('foo bar', …)</c> produced an attribute a browser refuses to create — and
    /// the one name that did fail, the empty string, threw a bare <c>Error</c> with no <c>name</c> or
    /// <c>code</c> for a caller to branch on rather than a <c>DOMException</c>.
    /// </remarks>
    internal JSValue SetAttribute(DomElement element, in Arguments a)
    {
        if (a.Length >= 2)
        {
            var name = a[0].ToString();
            DomBridge.ValidateAttributeName(name, _host.JsContext);
            SetAttributeLikeSetAttribute(element, name, a[1].ToString());
        }

        return JSUndefined.Value;
    }

    internal JSValue GetAttributeNode(DomElement element, JSObject? obj, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        var name = a[0].ToString();
        return DomBridge.TryGetAttribute(element, name, out var val) ? BuildAttrNode(name, val, element, obj) : JSNull.Value;
    }

    internal JSValue GetAttributeNodeNS(DomElement element, JSObject? obj, in Arguments a)
    {
        if (a.Length < 2)
            return JSNull.Value;
        var ns = a[0].IsNull || a[0].IsUndefined ? null : a[0].ToString();
        var localName = a[1].ToString();
        if (!DomBridge.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
            return JSNull.Value;
        return BuildAttrNode(qName, val, element, obj);
    }

    internal JSValue HasAttribute(DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSBoolean.False;
        return DomBridge.HasAttr(element, a[0].ToString()) ? JSBoolean.True : JSBoolean.False;
    }

    internal JSValue RemoveAttribute(DomElement element, in Arguments a)
    {
        if (a.Length > 0)
            RemoveAttributeLikeRemoveAttribute(element, a[0].ToString());
        return JSUndefined.Value;
    }

    /// <summary>
    /// <c>element.toggleAttribute(name, force)</c>. It validates the name the same way
    /// <see cref="SetAttribute"/> does — DOM §4.9.4 runs the identical check, and a browser throws
    /// from it, which was measured. <c>removeAttribute</c>, <c>hasAttribute</c> and
    /// <c>getAttribute</c> deliberately do not: they answer about a name rather than create one, and a
    /// browser accepts an invalid name from all three.
    /// </summary>
    internal JSValue ToggleAttribute(DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSBoolean.False;
        var attrName = a[0].ToString();
        DomBridge.ValidateAttributeName(attrName, _host.JsContext);
        var hasAttribute = DomBridge.HasAttr(element, attrName);
        var forceSpecified = a.Length > 1 && !a[1].IsUndefined;
        var shouldHaveAttribute = forceSpecified ? a[1].BooleanValue : !hasAttribute;
        if (shouldHaveAttribute)
        {
            if (!hasAttribute)
                SetAttributeLikeSetAttribute(element, attrName, string.Empty);
            return JSBoolean.True;
        }

        if (hasAttribute)
            RemoveAttributeLikeRemoveAttribute(element, attrName);
        return JSBoolean.False;
    }

    internal JSValue SetAttributeNode(DomElement element, JSObject? obj, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject attrObj)
            return JSNull.Value;
        var name = GetAttrNodeName(attrObj);
        if (string.IsNullOrEmpty(name))
            return JSNull.Value;
        var old = ReplacedAttrNode(element, name, attrObj, obj ?? JSNull.Value);
        SetAttributeLikeSetAttribute(element, name, attrObj[(KeyString)"value"].ToString());
        return old;
    }

    internal JSValue SetAttributeNodeNS(DomElement element, JSObject? obj, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject attrObj)
            return JSNull.Value;
        var name = GetAttrNodeName(attrObj);
        var localName = GetAttrNodeLocalName(attrObj);
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(localName))
            return JSNull.Value;
        var ns = GetAttrNodeNamespace(attrObj);
        var old = DomBridge.TryGetNsAttribute(element, ns, localName, out var oldQName, out _)
            ? ReplacedAttrNode(element, oldQName, attrObj, obj ?? JSNull.Value)
            : JSNull.Value;
        SetAttributeLikeSetAttributeNS(element, ns, name, localName, attrObj[(KeyString)"value"].ToString());
        return old;
    }

    internal JSValue RemoveAttributeNode(DomElement element, JSObject? obj, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject attrObj)
            return JSNull.Value;
        var name = GetAttrNodeName(attrObj);
        if (string.IsNullOrEmpty(name) || !DomBridge.TryGetAttribute(element, name, out var val))
            return JSNull.Value;
        var removed = BuildAttrNode(name, val, element, obj);
        RemoveAttributeLikeRemoveAttribute(element, name);
        return removed;
    }

    internal JSValue RemoveAttributeNodeNS(DomElement element, JSObject? obj, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject attrObj)
            return JSNull.Value;
        var localName = GetAttrNodeLocalName(attrObj);
        if (string.IsNullOrEmpty(localName))
            return JSNull.Value;
        var ns = GetAttrNodeNamespace(attrObj);
        if (!DomBridge.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
            return JSNull.Value;
        var removed = BuildAttrNode(qName, val, element, obj);
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
    internal JSValue SetAttributeNS(DomElement element, in Arguments a)
    {
        if (a.Length >= 3)
        {
            var ns = a[0].IsNull || a[0].IsUndefined ? null : a[0].ToString();
            var qName = a[1].ToString();
            var val = a[2].ToString();
            if (_host.JsContext is { } context)
                DomBridge.ValidateQualifiedName(qName, ns, context);
            var localName = qName.Contains(':') ? qName[(qName.IndexOf(':') + 1)..] : qName;
            SetAttributeLikeSetAttributeNS(element, ns, qName, localName, val);
        }

        return JSUndefined.Value;
    }

    internal JSValue GetAttributeNS(DomElement element, in Arguments a)
    {
        if (a.Length < 2)
            return JSNull.Value;
        var ns = a[0].IsNull || a[0].IsUndefined ? null : a[0].ToString();
        var localName = a[1].ToString();
        var val = element.GetAttributeNS(ns, localName);
        return val is not null ? new JSString(val) : JSNull.Value;
    }

    internal JSValue RemoveAttributeNS(DomElement element, in Arguments a)
    {
        if (a.Length >= 2)
        {
            var ns = a[0].IsNull || a[0].IsUndefined ? null : a[0].ToString();
            var localName = a[1].ToString();
            RemoveAttributeLikeRemoveAttributeNS(element, ns, localName);
        }

        return JSUndefined.Value;
    }

    internal JSValue HasAttributeNS(DomElement element, in Arguments a)
    {
        if (a.Length < 2)
            return JSBoolean.False;
        var ns = a[0].IsNull || a[0].IsUndefined ? null : a[0].ToString();
        var localName = a[1].ToString();
        return element.GetAttributeNS(ns, localName) is not null ? JSBoolean.True : JSBoolean.False;
    }
}
