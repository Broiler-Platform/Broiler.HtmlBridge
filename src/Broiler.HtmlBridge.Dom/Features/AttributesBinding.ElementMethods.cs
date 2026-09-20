using Broiler.JSeal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>Element</c> attribute methods — <c>getAttribute</c>, <c>setAttribute</c>, <c>toggleAttribute</c>,
/// the <c>Attr</c>-node and namespaced variants — that <c>Element.prototype</c> installs and
/// <see cref="AttributesBinding"/> backs.
/// </summary>
internal sealed partial class AttributesBinding
{
    // -------- Element attribute methods (element.getAttribute / setAttribute / … , registered on
    // Element.prototype by DomBridge/ElementInterface.cs; they delegate the write and Attr-node
    // construction into this module).
    //
    // Every argument read is the realm's ECMAScript conversion rather than the handle's rendering:
    // `el.getAttribute({toString(){…}})` has always run the object's own toString here. --------

    internal JsValue GetAttribute(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        var name = call.Realm.ToJsString(call[0]);
        return DomBridgeUtils.TryGetAttribute(element, name, out var val) ? JsValue.String(val) : JsValue.Null;
    }

    /// <summary>
    /// <c>element.setAttribute(name, value)</c> — DOM §4.9.1, which begins by requiring
    /// <paramref name="call" />'s name to match the XML <c>Name</c> production and throwing
    /// <c>InvalidCharacterError</c> when it does not.
    /// </summary>
    /// <remarks>
    /// The validation is not optional: written through silently, <c>setAttribute('@click', …)</c>
    /// and <c>setAttribute('foo bar', …)</c> would produce an attribute a browser refuses to create,
    /// and an empty name must raise a <c>DOMException</c> a caller can branch on by <c>name</c> and
    /// <c>code</c> rather than a bare <c>Error</c>.
    /// </remarks>
    internal JsValue SetAttribute(DomElement element, in JsCall call)
    {
        if (call.Length >= 2)
        {
            var name = call.Realm.ToJsString(call[0]);
            DomBridgeHostUtils.ValidateAttributeName(name, call.Realm);
            SetAttributeLikeSetAttribute(element, name, call.Realm.ToJsString(call[1]));
        }

        return JsValue.Undefined;
    }

    internal JsValue GetAttributeNode(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        var name = call.Realm.ToJsString(call[0]);
        return DomBridgeUtils.TryGetAttribute(element, name, out var val)
            ? BuildAttrNode(name, val, element, ownerObj)
            : JsValue.Null;
    }

    internal JsValue GetAttributeNodeNS(DomElement element, JsValue ownerObj, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Null;
        var ns = call[0].IsNullish ? null : call.Realm.ToJsString(call[0]);
        var localName = call.Realm.ToJsString(call[1]);
        if (!DomBridgeUtils.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
            return JsValue.Null;
        return BuildAttrNode(qName, val, element, ownerObj);
    }

    internal JsValue HasAttribute(DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.False;
        return JsValue.Boolean(DomBridgeUtils.HasAttr(element, call.Realm.ToJsString(call[0])));
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
        DomBridgeHostUtils.ValidateAttributeName(attrName, call.Realm);
        var hasAttribute = DomBridgeUtils.HasAttr(element, attrName);
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
        var old = DomBridgeUtils.TryGetNsAttribute(element, ns, localName, out var oldQName, out _)
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
        if (string.IsNullOrEmpty(name) || !DomBridgeUtils.TryGetAttribute(element, name, out var val))
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
        if (!DomBridgeUtils.TryGetNsAttribute(element, ns, localName, out var qName, out var val))
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
            // The realm of the call rather than the host's, and unguarded: a JsCall exists only
            // because guest code is running, so the realm it carries cannot be absent.
            DomBridgeUtils.ValidateQualifiedName(qName, ns, call.Realm);
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
