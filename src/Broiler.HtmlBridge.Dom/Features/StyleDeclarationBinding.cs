using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The CSSOM <c>CSSStyleDeclaration</c> feature binding (HtmlBridge complexity-reduction roadmap Phase 3,
/// P3.14) — the JS style-declaration object in its three flavours: the writable <c>element.style</c>
/// (backed by the element's inline-style map), the writable rule declaration (<c>rule.style</c>, backed
/// by a plain property map) and the read-only <c>getComputedStyle</c> result (built from an
/// engine-produced computed map). Each exposes cssText/setProperty/getPropertyValue/removeProperty/
/// cssFloat/length/item/getPropertyPriority/parentRule plus camelCase↔kebab-case bracket access.
/// <para>
/// It is the cleanest kind of slice: pure CSSOM-IDL logic over an inline-style dictionary and the
/// canonical <see cref="Broiler.CSS.CssPropertyNames"/>/<see cref="Broiler.CSS.CssPriority"/> helpers, so
/// — like <see cref="ClassListBinding"/> — it is an <b>internal static class with no host contract</b>
/// beyond <see cref="IInlineStyleHost"/>. The map <em>production</em> (the inline-style store, the
/// engine cascade for computed style) and the invalidation side effects stay in the bridge: callers pass
/// the computed map, an <c>onMutation</c> callback and (for inline declarations) an
/// <c>onPositionAreaInvalidate</c> callback that clears the bridge-instance position-area memo, and the
/// module reaches the shared inline-style store and the "set via JS" bookkeeping through the neutral
/// static <c>DomBridge</c> helpers (<c>InlineStyle</c>, <c>ParseStyle</c>, <c>IsAcceptableInlineValue</c>,
/// <c>ExpandCssShorthands</c>, <c>Mark/Unmark/Clear/InlineStylePropsSetByJs</c>).
/// </para>
/// </summary>
/// <remarks>
/// <para>
/// <b>The two writable declarations are <see cref="IJsExotic"/> handlers rather than engine
/// subclasses.</b> <c>el.style.backgroundColor</c> and <c>el.style["background-color"]</c> address the
/// same CSS property, which no fixed list of members can express, so the declaration used to derive from
/// the engine's own object type and override its lookup protocol. It now declares the hook instead:
/// <see cref="IJsRealm.NewExotic"/> takes the handler and the provider owns the protocol. The ordering
/// the subclasses established is the ordering the contract mandates — <b>ordinary properties are
/// consulted first and the handler answers only what they did not</b>, so a CSS property can never
/// shadow <c>setProperty</c> — and a named <em>write</em> is offered to the handler first, because the
/// declaration has to see the value before it becomes an ordinary property.
/// </para>
/// <para>
/// <b>One widening, deliberate and reported.</b> The old <c>GetValue</c> answered the empty string for
/// <em>any</em> name it did not otherwise resolve, so <c>el.style.notAProperty</c> is <c>""</c> rather
/// than <c>undefined</c> — which is what a page's feature detection reads. Preserving that means
/// <see cref="IJsExotic.TryGetNamed"/> always answers, and the same hook also backs <c>in</c> and
/// <c>Object.getOwnPropertyDescriptor</c>, which the subclass did not override. Those two now answer for
/// any name where they used to answer only for the declaration's own members. The alternative — declining
/// unknown names — would turn every unresolved read into <c>undefined</c>, which is the far more
/// load-bearing of the two. Enumeration is unaffected: <see cref="IJsExotic.SupportedNames"/> is empty,
/// exactly as the subclass supplied no keys of its own.
/// </para>
/// </remarks>
internal static partial class StyleDeclarationBinding
{
    // Names that are JS methods / special properties on a declaration object, not CSS properties.
    private static readonly HashSet<string> NonCssNames = new(StringComparer.Ordinal)
    {
        "setProperty", "getPropertyValue", "removeProperty",
        "cssText", "cssFloat", "length", "parentRule",
        "item", "getPropertyPriority",
    };

    /// <summary>
    /// The CSS property a CSSOM attribute name addresses — <see cref="CssPropertyNames.ToCssPropertyName"/>
    /// plus the <em>webkit-cased attribute</em> of CSSOM §4.2.
    /// </summary>
    /// <remarks>
    /// CSSOM defines two IDL attributes per vendor-prefixed property: the
    /// camel-cased <c>WebkitLineClamp</c> and the webkit-cased
    /// <c>webkitLineClamp</c>, both addressing <c>-webkit-line-clamp</c>. Only the
    /// first falls out of the shared uppercase-to-hyphen rule; the second — the
    /// spelling authors and libraries actually write — came back as
    /// <c>webkit-line-clamp</c>, a property no part of the engine knows, so
    /// <c>el.style.webkitLineClamp = …</c> was accepted and then silently had no
    /// effect. Applied here, at the CSSOM boundary, rather than in the shared
    /// helper: the rule is a property of this IDL surface, and the same helper
    /// converts names on paths where a leading <c>webkit</c> is just a name.
    /// </remarks>
    private static string ToCssPropertyName(string domName)
    {
        if (domName.Length > 6
            && domName.StartsWith("webkit", StringComparison.Ordinal)
            && char.IsUpper(domName[6]))
        {
            return "-" + CssPropertyNames.ToCssPropertyName(domName);
        }

        return CssPropertyNames.ToCssPropertyName(domName);
    }

    // -------- element.style (writable, inline-style store) --------

    /// <summary>Builds the writable <c>element.style</c> CSSStyleDeclaration.</summary>
    internal static JsValue BuildInlineDeclaration(IJsRealm realm, IInlineStyleHost host, DomElement element,
        Action? onMutation = null, JsValue parentRule = default,
        Action<DomElement>? onPositionAreaInvalidate = null)
    {
        var style = realm.NewExotic(new InlineDeclaration(realm, host, element, onMutation, onPositionAreaInvalidate));

        realm.DefineAccessor(style, "cssText",
            (in _) => InlineGetCssText(host, element),
            (in call) => InlineSetCssText(host, element, onMutation, in call));

        realm.DefineValue(style, "setProperty",
            realm.NewMethod("setProperty", (in call) => InlineSetProperty(host, element, onMutation, in call), 2));

        realm.DefineValue(style, "getPropertyValue",
            realm.NewMethod("getPropertyValue", (in call) => InlineGetPropertyValue(host, element, in call), 1));

        realm.DefineValue(style, "removeProperty",
            realm.NewMethod("removeProperty", (in call) => InlineRemoveProperty(host, element, onMutation, in call), 1));

        realm.DefineAccessor(style, "cssFloat",
            (in _) => InlineGetCssFloat(host, element),
            (in call) => InlineSetCssFloat(host, element, onMutation, in call));

        realm.DefineAccessor(style, "length",
            (in _) => JsValue.Number(GetStylePropertyNames(host, element).Count), null);

        realm.DefineValue(style, "item",
            realm.NewMethod("item", (in call) => InlineItem(host, element, in call), 1));

        realm.DefineValue(style, "getPropertyPriority",
            realm.NewMethod("getPropertyPriority", (in call) => InlineGetPropertyPriority(host, element, in call), 1));

        realm.DefineAccessor(style, "parentRule",
            (in _) => parentRule.IsMissing ? JsValue.Null : parentRule, null);

        return style;
    }

    /// <summary>Builds the writable rule (<c>CSSRule.style</c>) CSSStyleDeclaration over a property map.
    /// Was <c>DomBridge.BuildStyleObject(styleMap, parentRule)</c>.</summary>
    internal static JsValue BuildRuleDeclaration(IJsRealm realm, Dictionary<string, string> styleMap,
        JsValue parentRule = default)
    {
        var style = realm.NewExotic(new RuleDeclaration(realm, styleMap));

        realm.DefineAccessor(style, "cssText",
            (in _) => RuleGetCssText(styleMap),
            (in call) => RuleSetCssText(styleMap, in call));

        realm.DefineValue(style, "setProperty",
            realm.NewMethod("setProperty", (in call) => RuleSetProperty(styleMap, in call), 2));

        realm.DefineValue(style, "getPropertyValue",
            realm.NewMethod("getPropertyValue", (in call) => RuleGetPropertyValue(styleMap, in call), 1));

        realm.DefineValue(style, "removeProperty",
            realm.NewMethod("removeProperty", (in call) => RuleRemoveProperty(styleMap, in call), 1));

        realm.DefineAccessor(style, "cssFloat",
            (in _) => RuleGetCssFloat(styleMap),
            (in call) => RuleSetCssFloat(styleMap, in call));

        realm.DefineAccessor(style, "length",
            (in _) => JsValue.Number(GetStylePropertyNames(styleMap).Count), null);

        realm.DefineValue(style, "item",
            realm.NewMethod("item", (in call) => RuleItem(styleMap, in call), 1));

        realm.DefineValue(style, "getPropertyPriority",
            realm.NewMethod("getPropertyPriority", (in call) => RuleGetPropertyPriority(styleMap, in call), 1));

        realm.DefineAccessor(style, "parentRule",
            (in _) => parentRule.IsMissing ? JsValue.Null : parentRule, null);

        return style;
    }

    /// <summary>Builds the read-only <c>getComputedStyle</c> declaration from an engine-produced
    /// <paramref name="computed"/> map (the bridge still produces the map). Was the object-construction
    /// half of <c>DomBridge.BuildComputedStyleObject</c>.</summary>
    /// <remarks>
    /// An ordinary object, not an exotic: a computed declaration is a snapshot, so every property it
    /// answers is installed up front and nothing has to complete a lookup. That is what it always was.
    /// </remarks>
    internal static JsValue BuildComputedDeclaration(IJsRealm realm, Dictionary<string, string> computed)
    {
        var propertyNames = computed.Keys.ToList();
        var obj = realm.NewObject();

        // Expose all computed properties as both camelCase and kebab-case
        foreach (var kv in computed)
        {
            var camel = CssPropertyNames.ToDomPropertyName(kv.Key);
            var normalized = CssPriority.Strip(kv.Value);
            realm.DefineValue(obj, kv.Key, JsValue.String(normalized));
            if (camel != kv.Key)
                realm.DefineValue(obj, camel, JsValue.String(normalized));
        }

        // getPropertyValue method (supports both kebab-case and camelCase lookups)
        realm.DefineValue(obj, "getPropertyValue",
            realm.NewMethod("getPropertyValue", (in call) => ComputedGetPropertyValue(computed, in call), 1));
        realm.DefineAccessor(obj, "length", (in _) => JsValue.Number(propertyNames.Count), null);

        realm.DefineValue(obj, "item",
            realm.NewMethod("item", (in call) => ComputedItem(propertyNames, in call), 1));
        realm.DefineValue(obj, "getPropertyPriority",
            realm.NewMethod("getPropertyPriority", (in _) => JsValue.String(string.Empty), 1));

        realm.DefineAccessor(obj, "parentRule", (in _) => JsValue.Null, null);

        return obj;
    }

    // -------- declaration lookup handlers --------

    /// <summary>
    /// The <c>element.style</c> named-property hook: a CSS property read out of the inline-style store,
    /// and a write that reaches the store before it becomes an ordinary property.
    /// </summary>
    private sealed class InlineDeclaration(
        IJsRealm realm,
        IInlineStyleHost host,
        DomElement element,
        Action? onMutation,
        Action<DomElement>? onPositionAreaInvalidate) : IJsExotic
    {
        /// <inheritdoc />
        /// <remarks>
        /// Reached only for a name the ordinary properties did not answer, which is the whole of what
        /// the old override's leading <c>base.GetValue</c> established. It always answers — with the
        /// property's value when there is one and the empty string when there is not — because that is
        /// what the override did; see the remarks on <see cref="StyleDeclarationBinding"/>.
        /// </remarks>
        public bool TryGetNamed(string name, out JsValue value)
        {
            if (!NonCssNames.Contains(name) &&
                TryGetStylePropertyRawValue(host, element, name, out var raw))
            {
                value = JsValue.String(CssPriority.Strip(raw));
                return true;
            }

            value = JsValue.String(string.Empty);
            return true;
        }

        /// <summary>A declaration has no indexed properties — <c>item(i)</c> is the indexed read.</summary>
        public bool TryGetIndex(uint index, out JsValue value)
        {
            value = JsValue.Missing;
            return false;
        }

        /// <inheritdoc />
        /// <remarks>
        /// <b>Declining is the normal outcome, and it has to be.</b> The old override did its CSS work
        /// and then fell through to <c>base.SetValue</c>, so the assigned value also landed as an
        /// ordinary property and <c>getPropertyValue</c>'s receiver-read could find it. Answering
        /// <see langword="false"/> here is exactly that fall-through. The one case that answers
        /// <see langword="true"/> is a value CSSOM rejects: intercepting it is what keeps the rejected
        /// value from being stored as an ordinary property and resurfacing from the getter.
        /// </remarks>
        public bool TrySetNamed(string name, JsValue value)
        {
            if (NonCssNames.Contains(name))
                return false;

            var kebab = ToCssPropertyName(name);

            // The realm's ToString, not the handle's: an object assigned to a CSS property runs its own
            // toString, and that is the coercion the page observes.
            var val = value.IsMissing ? string.Empty : realm.ToJsString(value);
            if (string.IsNullOrEmpty(val))
            {
                host.InlineStyle(element).Remove(kebab);
                host.UnmarkInlineStylePropSetByJs(element, kebab);
            }
            else if (DomBridge.IsAcceptableInlineValue(kebab, val))
            {
                host.InlineStyle(element)[kebab] = val;
                host.MarkInlineStylePropSetByJs(element, kebab);
            }
            else
            {
                // Invalid value: ignore it completely (CSSOM error handling). Intercepted rather than
                // declined — the getter reads the JS property first, so letting the ordinary assignment
                // keep the value would resurface the rejected value. Any existing valid value is left
                // intact.
                return true;
            }

            // Invalidate cached position-area resolution when relevant properties change so offset
            // queries recompute. The memo is a per-bridge-instance table, so the owning bridge threads
            // its clear in via onPositionAreaInvalidate (was a static DomBridge.ClearPositionAreaResolution).
            if (kebab is "position-area" or "position-anchor")
                onPositionAreaInvalidate?.Invoke(element);

            onMutation?.Invoke();
            return false;
        }

        /// <summary>
        /// None: the old subclass overrode no key enumeration, so a declaration enumerates its ordinary
        /// properties and nothing else.
        /// </summary>
        public IReadOnlyList<string> SupportedNames => [];

        /// <inheritdoc />
        public uint IndexedLength => 0;
    }

    /// <summary>The <c>rule.style</c> named-property hook, over a plain property map.</summary>
    private sealed class RuleDeclaration(IJsRealm realm, Dictionary<string, string> style) : IJsExotic
    {
        /// <inheritdoc />
        public bool TryGetNamed(string name, out JsValue value)
        {
            if (!NonCssNames.Contains(name) &&
                TryGetStylePropertyRawValue(style, name, out var raw))
            {
                value = JsValue.String(CssPriority.Strip(raw));
                return true;
            }

            value = JsValue.String(string.Empty);
            return true;
        }

        /// <inheritdoc />
        public bool TryGetIndex(uint index, out JsValue value)
        {
            value = JsValue.Missing;
            return false;
        }

        /// <inheritdoc />
        public bool TrySetNamed(string name, JsValue value)
        {
            if (NonCssNames.Contains(name))
                return false;

            var kebab = ToCssPropertyName(name);
            var val = value.IsMissing ? string.Empty : realm.ToJsString(value);
            if (string.IsNullOrEmpty(val))
                style.Remove(kebab);
            else if (DomBridge.IsAcceptableInlineValue(kebab, val))
                style[kebab] = val;
            else
                return true;   // invalid value ignored; don't store it as an ordinary property either

            return false;
        }

        /// <inheritdoc />
        public IReadOnlyList<string> SupportedNames => [];

        /// <inheritdoc />
        public uint IndexedLength => 0;
    }

    // -------- shared property-name / raw-value helpers (exclusive to the declaration surface) --------

    private static List<string> GetStylePropertyNames(IReadOnlyDictionary<string, string> style) => [.. style.Keys];

    private static List<string> GetStylePropertyNames(IInlineStyleHost host, DomElement element) => GetStylePropertyNames(host.InlineStyle(element));

    private static Dictionary<string, string> BuildDeclaredInlineStyleMap(IInlineStyleHost host, DomElement element)
    {
        var declared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (DomBridge.TryGetAttribute(element, "style", out var inlineStyle) &&
            !string.IsNullOrEmpty(inlineStyle))
        {
            foreach (var kv in DomBridge.ParseStyle(inlineStyle))
                declared[kv.Key] = kv.Value;
        }

        foreach (var property in host.InlineStylePropsSetByJs(element))
        {
            if (host.InlineStyle(element).TryGetValue(property, out var value))
                declared[property] = value;
        }

        return declared;
    }

    private static bool TryGetExpandedInlineStyleRawValue(IInlineStyleHost host, DomElement element, string property, out string value)
    {
        var declared = BuildDeclaredInlineStyleMap(host, element);
        if (declared.Count == 0)
        {
            value = string.Empty;
            return false;
        }

        DomBridge.ExpandCssShorthands(declared);

        if (declared.TryGetValue(property, out value!))
            return true;

        var camel = CssPropertyNames.ToDomPropertyName(property);
        if (camel != property && declared.TryGetValue(camel, out value!))
            return true;

        var kebab = ToCssPropertyName(property);
        if (kebab != property && declared.TryGetValue(kebab, out value!))
            return true;

        value = string.Empty;
        return false;
    }

    private static bool TryGetStylePropertyRawValue(IReadOnlyDictionary<string, string> style, string property, out string value)
    {
        if (style.TryGetValue(property, out value!))
            return true;

        var camel = CssPropertyNames.ToDomPropertyName(property);
        if (camel != property && style.TryGetValue(camel, out value!))
            return true;

        var kebab = ToCssPropertyName(property);
        if (kebab != property && style.TryGetValue(kebab, out value!))
            return true;

        value = string.Empty;
        return false;
    }

    private static bool TryGetStylePropertyRawValue(IInlineStyleHost host, DomElement element, string property, out string value)
    {
        if (TryGetStylePropertyRawValue(host.InlineStyle(element), property, out value!))
            return true;

        return TryGetExpandedInlineStyleRawValue(host, element, property, out value!);
    }
}
