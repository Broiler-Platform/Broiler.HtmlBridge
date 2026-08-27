using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;
using Broiler.CSS;

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
/// — like <see cref="ClassListBinding"/> — it is an <b>internal static class with no host contract</b>.
/// The map <em>production</em> (the inline-style store, the engine cascade for computed style) and the
/// invalidation side effects stay in the bridge: callers pass the computed map, an <c>onMutation</c>
/// callback and (for inline declarations) an <c>onPositionAreaInvalidate</c> callback that clears the
/// bridge-instance position-area memo, and the module reaches the shared inline-style store and the
/// "set via JS" bookkeeping through the neutral static <c>DomBridge</c> helpers (<c>InlineStyle</c>,
/// <c>ParseStyle</c>, <c>IsAcceptableInlineValue</c>, <c>ExpandCssShorthands</c>,
/// <c>Mark/Unmark/Clear/InlineStylePropsSetByJs</c>).
/// </para>
/// </summary>
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

    /// <summary>Builds the writable <c>element.style</c> CSSStyleDeclaration. Was
    /// <c>DomBridge.BuildStyleObject(element, onMutation, parentRule)</c>.</summary>
    internal static JSObject BuildInlineDeclaration(IInlineStyleHost host, DomElement element, Action? onMutation = null, JSValue? parentRule = null,
        Action<DomElement>? onPositionAreaInvalidate = null)
    {
        var style = new CssStyleDeclaration(host, element, onMutation, onPositionAreaInvalidate);

        style.FastAddProperty("cssText",
            new DomFunction((in a) => InlineGetCssText(host, element, in a), "get cssText"),
            new DomFunction((in a) => InlineSetCssText(host, element, onMutation, in a), "set cssText"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        style.FastAddValue("setProperty",
            new DomFunction((in a) => InlineSetProperty(host, element, onMutation, in a), "setProperty", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddValue("getPropertyValue",
            new DomFunction((in a) => InlineGetPropertyValue(host, element, in a), "getPropertyValue", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddValue("removeProperty",
            new DomFunction((in a) => InlineRemoveProperty(host, element, onMutation, in a), "removeProperty", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddProperty("cssFloat",
            new DomFunction((in a) => InlineGetCssFloat(host, element, in a), "get cssFloat"),
            new DomFunction((in a) => InlineSetCssFloat(host, element, onMutation, in a), "set cssFloat"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        style.FastAddProperty("length",
            new DomFunction((in _) => new JSNumber(GetStylePropertyNames(host, element).Count), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        style.FastAddValue("item",
            new DomFunction((in a) => InlineItem(host, element, in a), "item", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddValue("getPropertyPriority",
            new DomFunction((in a) => InlineGetPropertyPriority(host, element, in a), "getPropertyPriority", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddProperty("parentRule",
            new DomFunction((in _) => parentRule ?? JSNull.Value, "get parentRule"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        return style;
    }

    /// <summary>Builds the writable rule (<c>CSSRule.style</c>) CSSStyleDeclaration over a property map.
    /// Was <c>DomBridge.BuildStyleObject(styleMap, parentRule)</c>.</summary>
    internal static JSObject BuildRuleDeclaration(Dictionary<string, string> styleMap, JSValue? parentRule = null)
    {
        var style = new CssRuleStyleDeclaration(styleMap);

        style.FastAddProperty("cssText",
            new DomFunction((in _) => RuleGetCssText(styleMap, in _), "get cssText"),
            new DomFunction((in a) => RuleSetCssText(styleMap, in a), "set cssText"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        style.FastAddValue("setProperty",
            new DomFunction((in a) => RuleSetProperty(styleMap, in a), "setProperty", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddValue("getPropertyValue",
            new DomFunction((in a) => RuleGetPropertyValue(styleMap, in a), "getPropertyValue", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddValue("removeProperty",
            new DomFunction((in a) => RuleRemoveProperty(styleMap, in a), "removeProperty", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddProperty("cssFloat",
            new DomFunction((in _) => RuleGetCssFloat(styleMap, in _), "get cssFloat"),
            new DomFunction((in a) => RuleSetCssFloat(styleMap, in a), "set cssFloat"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        style.FastAddProperty("length",
            new DomFunction((in _) => new JSNumber(GetStylePropertyNames(styleMap).Count), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        style.FastAddValue("item",
            new DomFunction((in a) => RuleItem(styleMap, in a), "item", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddValue("getPropertyPriority",
            new DomFunction((in a) => RuleGetPropertyPriority(styleMap, in a), "getPropertyPriority", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        style.FastAddProperty("parentRule",
            new DomFunction((in _) => parentRule ?? JSNull.Value, "get parentRule"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        return style;
    }

    /// <summary>Builds the read-only <c>getComputedStyle</c> declaration from an engine-produced
    /// <paramref name="computed"/> map (the bridge still produces the map). Was the object-construction
    /// half of <c>DomBridge.BuildComputedStyleObject</c>.</summary>
    internal static JSObject BuildComputedDeclaration(Dictionary<string, string> computed)
    {
        var propertyNames = computed.Keys.ToList();
        var obj = new JSObject();

        // Expose all computed properties as both camelCase and kebab-case
        foreach (var kv in computed)
        {
            var camel = CssPropertyNames.ToDomPropertyName(kv.Key);
            var normalized = CssPriority.Strip(kv.Value);
            obj.FastAddValue(kv.Key, new JSString(normalized), JSPropertyAttributes.EnumerableConfigurableValue);
            if (camel != kv.Key)
                obj.FastAddValue(camel, new JSString(normalized), JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // getPropertyValue method (supports both kebab-case and camelCase lookups)
        obj.FastAddValue("getPropertyValue", new DomFunction((in a) => ComputedGetPropertyValue(computed, in a), "getPropertyValue", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddProperty("length", new DomFunction((in _) => new JSNumber(propertyNames.Count), "get length"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddValue("item", new DomFunction((in a) => ComputedItem(propertyNames, in a), "item", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("getPropertyPriority", new DomFunction((in _) => new JSString(string.Empty), "getPropertyPriority", 1), JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddProperty("parentRule", DomBridge.NullFunction("get parentRule"), null, JSPropertyAttributes.EnumerableConfigurableProperty);

        return obj;
    }

    // -------- declaration JS object types --------

    private sealed class CssStyleDeclaration(IInlineStyleHost host, DomElement element, Action? onMutation = null,
        Action<DomElement>? onPositionAreaInvalidate = null) : JSObject
    {
        protected override bool SetValue(KeyString name, JSValue value, JSValue receiver, bool throwError = true)
        {
            var nameStr = name.ToString();
            if (!NonCssNames.Contains(nameStr))
            {
                var kebab = ToCssPropertyName(nameStr);
                var val = value?.ToString() ?? string.Empty;
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
                    // Invalid value: ignore it completely (CSSOM error handling). Return
                    // without falling through to base.SetValue — the getter reads the JS
                    // property first, so letting the base object keep the value would
                    // resurface the rejected value. Any existing valid value is left intact.
                    return true;
                }

                // Invalidate cached position-area resolution when relevant
                // properties change so offset queries recompute. The memo is now a
                // per-bridge-instance table, so the owning bridge threads its clear in
                // via onPositionAreaInvalidate (was a static DomBridge.ClearPositionAreaResolution).
                if (kebab is "position-area" or "position-anchor")
                    onPositionAreaInvalidate?.Invoke(element);

                onMutation?.Invoke();
            }

            return base.SetValue(name, value, receiver, throwError);
        }

        protected override JSValue GetValue(KeyString key, JSValue receiver, bool throwError = true)
        {
            // Try normal lookup first (methods, explicit properties, etc.)
            var result = base.GetValue(key, receiver, false);
            if (result != null && !result.IsUndefined)
                return result;

            // Fall back to InlineStyle(element) lookup (kebab-case)
            var nameStr = key.ToString();
            if (!NonCssNames.Contains(nameStr))
            {
                if (TryGetStylePropertyRawValue(host, element, nameStr, out var val))
                    return new JSString(CssPriority.Strip(val));
            }

            return new JSString(string.Empty);
        }
    }

    private sealed class CssRuleStyleDeclaration(Dictionary<string, string> style) : JSObject
    {
        protected override bool SetValue(KeyString name, JSValue value, JSValue receiver, bool throwError = true)
        {
            var nameStr = name.ToString();
            if (!NonCssNames.Contains(nameStr))
            {
                var kebab = ToCssPropertyName(nameStr);
                var val = value?.ToString() ?? string.Empty;
                if (string.IsNullOrEmpty(val))
                    style.Remove(kebab);
                else if (DomBridge.IsAcceptableInlineValue(kebab, val))
                    style[kebab] = val;
                else
                    return true;   // invalid value ignored; don't store it as a JS property either
            }

            return base.SetValue(name, value, receiver, throwError);
        }

        protected override JSValue GetValue(KeyString key, JSValue receiver, bool throwError = true)
        {
            var result = base.GetValue(key, receiver, false);
            if (result != null && !result.IsUndefined)
                return result;

            var nameStr = key.ToString();
            if (!NonCssNames.Contains(nameStr) &&
                TryGetStylePropertyRawValue(style, nameStr, out var val))
            {
                return new JSString(CssPriority.Strip(val));
            }

            return new JSString(string.Empty);
        }
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
