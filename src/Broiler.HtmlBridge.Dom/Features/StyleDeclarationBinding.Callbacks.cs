using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <see cref="StyleDeclarationBinding"/> — the per-method CSSSStyleDeclaration callbacks, in three
/// families: <c>Inline*</c> (the writable <c>element.style</c>, over the inline-style store),
/// <c>Rule*</c> (the writable rule declaration, over a property map) and <c>Computed*</c> (the read-only
/// getComputedStyle result). Was the numbered <c>JsUtilities…003…023Core</c> / <c>JsCss…001/003Core</c>
/// callbacks.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's: each callback reads its arguments off the
/// <see cref="JsCall"/> frame and coerces them through the frame's realm, so a string argument runs the
/// same ECMAScript <c>ToString</c>/<c>ToNumber</c> a page observed before. A callback that never looked
/// at its arguments no longer takes a frame at all.
/// </remarks>
internal static partial class StyleDeclarationBinding
{
    // -------- element.style (writable, inline-style store) --------

    private static JsValue InlineGetCssText(IInlineStyleHost host, DomElement element)
    {
        var parts = host.InlineStyle(element).Select(kv => $"{kv.Key}: {kv.Value}");
        var text = string.Join("; ", parts);
        return JsValue.String(text.Length > 0 ? text + ";" : text);
    }

    private static JsValue InlineSetCssText(IInlineStyleHost host, DomElement element, Action? onMutation, in JsCall call)
    {
        host.InlineStyle(element).Clear();
        host.ClearInlineStylePropsSetByJs(element);
        if (call.Length > 0)
        {
            foreach (var kv in DomBridge.ParseStyle(call.Realm.ToJsString(call[0]), reportDrops: true))
            {
                host.InlineStyle(element)[kv.Key] = kv.Value;
                host.MarkInlineStylePropSetByJs(element, kv.Key);
            }
        }

        onMutation?.Invoke();
        return JsValue.Undefined;
    }

    /// <summary>
    /// The <c>element.style = "prop: val; ..."</c> assignment setter: in browsers <c>element.style</c> is
    /// effectively read-only, so assigning a string parses it as <c>cssText</c>. Unlike
    /// <see cref="InlineSetCssText"/> (the <c>style.cssText =</c> path, which stringifies any value), this
    /// only acts on a string right-hand side — a quirk preserved verbatim from the bridge's original
    /// <c>element.style</c> setter, which is why the caller tests for a string before calling and why the
    /// clear below happens only once it has.
    /// </summary>
    internal static void SetInlineStyleCssText(IInlineStyleHost host, DomElement element, Action? onMutation, string cssText)
    {
        host.InlineStyle(element).Clear();
        host.ClearInlineStylePropsSetByJs(element);
        foreach (var kv in DomBridge.ParseStyle(cssText, reportDrops: true))
        {
            host.InlineStyle(element)[kv.Key] = kv.Value;
            host.MarkInlineStylePropSetByJs(element, kv.Key);
        }

        onMutation?.Invoke();
    }

    private static JsValue InlineSetProperty(IInlineStyleHost host, DomElement element, Action? onMutation, in JsCall call)
    {
        if (call.Length >= 2)
        {
            var prop = call.Realm.ToJsString(call[0]);
            var value = CssPriority.Apply(
                call.Realm.ToJsString(call[1]),
                call.Length >= 3 ? call.Realm.ToJsString(call[2]) : string.Empty);
            if (string.IsNullOrEmpty(value))
            {
                host.InlineStyle(element).Remove(prop);
                host.UnmarkInlineStylePropSetByJs(element, prop);
            }
            else if (DomBridge.IsAcceptableInlineValue(prop, value))
            {
                host.InlineStyle(element)[prop] = value;
                host.MarkInlineStylePropSetByJs(element, prop);
            }
            // setProperty with an invalid value is a no-op per CSSOM (the value is not set).

            onMutation?.Invoke();
        }

        return JsValue.Undefined;
    }

    private static JsValue InlineGetPropertyValue(IInlineStyleHost host, DomElement element, in JsCall call)
    {
        if (call.Length > 0)
        {
            var prop = call.Realm.ToJsString(call[0]);
            if (TryGetStylePropertyRawValue(host, element, prop, out var val))
                return JsValue.String(CssPriority.Strip(val));
            // Try camelCase version of kebab-case input
            var camel = CssPropertyNames.ToDomPropertyName(prop);
            // Check the declaration object's own properties (set via el.style.propertyName = value)
            if (TryReadFromReceiver(in call, camel, out var jsVal) ||
                TryReadFromReceiver(in call, prop, out jsVal))
            {
                return jsVal;
            }
        }

        return JsValue.String(string.Empty);
    }

    /// <summary>
    /// Reads <paramref name="name"/> off the operation's receiver, answering <see langword="false"/> when
    /// there is no receiver or the read produced nothing usable.
    /// </summary>
    /// <remarks>
    /// <see cref="JsValue.Missing"/> stands where the engine handed back a CLR <see langword="null"/>: a
    /// call with no receiver at all, which the <c>a.This?[…]</c> this replaces guarded with a null-
    /// conditional. Undefined and null are rejected as they were, so an absent property falls through to
    /// the empty string rather than being answered with <c>undefined</c>.
    /// </remarks>
    private static bool TryReadFromReceiver(in JsCall call, string name, out JsValue value)
    {
        value = JsValue.Missing;
        if (call.This.IsMissing)
            return false;

        value = call.Realm.GetProperty(call.This, name);
        return !value.IsMissing && !value.IsUndefined && !value.IsNull;
    }

    private static JsValue InlineRemoveProperty(IInlineStyleHost host, DomElement element, Action? onMutation, in JsCall call)
    {
        if (call.Length > 0)
        {
            var prop = call.Realm.ToJsString(call[0]);
            var removed = host.InlineStyle(element).TryGetValue(prop, out var val) ? val : string.Empty;
            host.InlineStyle(element).Remove(prop);
            host.UnmarkInlineStylePropSetByJs(element, prop);
            onMutation?.Invoke();
            return JsValue.String(removed);
        }

        return JsValue.String(string.Empty);
    }

    private static JsValue InlineGetCssFloat(IInlineStyleHost host, DomElement element)
    {
        if (host.InlineStyle(element).TryGetValue("float", out var val))
            return JsValue.String(val);
        return JsValue.String(string.Empty);
    }

    private static JsValue InlineSetCssFloat(IInlineStyleHost host, DomElement element, Action? onMutation, in JsCall call)
    {
        if (call.Length > 0)
        {
            var val = call.Realm.ToJsString(call[0]);
            if (string.IsNullOrEmpty(val) || DomBridge.IsAcceptableInlineValue("float", val))
                host.InlineStyle(element)["float"] = val;
        }
        onMutation?.Invoke();
        return JsValue.Undefined;
    }

    private static JsValue InlineItem(IInlineStyleHost host, DomElement element, in JsCall call)
    {
        if (call.Length > 0 && int.TryParse(call.Realm.ToJsString(call[0]), out var index))
        {
            var propertyNames = GetStylePropertyNames(host, element);
            if (index >= 0 && index < propertyNames.Count)
                return JsValue.String(propertyNames[index]);
        }

        return JsValue.String(string.Empty);
    }

    private static JsValue InlineGetPropertyPriority(IInlineStyleHost host, DomElement element, in JsCall call)
    {
        if (call.Length > 0 &&
            TryGetStylePropertyRawValue(host, element, call.Realm.ToJsString(call[0]), out var value))
        {
            return JsValue.String(CssPriority.Parse(value));
        }

        return JsValue.String(string.Empty);
    }

    // -------- rule.style (writable, property map) --------

    private static JsValue RuleGetCssText(Dictionary<string, string> styleMap)
    {
        var parts = styleMap.Select(kv => $"{kv.Key}: {kv.Value}");
        var text = string.Join("; ", parts);
        return JsValue.String(text.Length > 0 ? text + ";" : text);
    }

    private static JsValue RuleSetCssText(Dictionary<string, string> styleMap, in JsCall call)
    {
        styleMap.Clear();
        if (call.Length > 0)
        {
            foreach (var kv in DomBridge.ParseStyle(call.Realm.ToJsString(call[0])))
                styleMap[kv.Key] = kv.Value;
        }

        return JsValue.Undefined;
    }

    private static JsValue RuleSetProperty(Dictionary<string, string> styleMap, in JsCall call)
    {
        if (call.Length >= 2)
        {
            var prop = call.Realm.ToJsString(call[0]);
            var value = CssPriority.Apply(
                call.Realm.ToJsString(call[1]),
                call.Length >= 3 ? call.Realm.ToJsString(call[2]) : string.Empty);
            if (string.IsNullOrEmpty(value))
                styleMap.Remove(prop);
            else if (DomBridge.IsAcceptableInlineValue(prop, value))
                styleMap[prop] = value;
            // setProperty with an invalid value is a no-op per CSSOM.
        }

        return JsValue.Undefined;
    }

    private static JsValue RuleGetPropertyValue(Dictionary<string, string> styleMap, in JsCall call)
    {
        if (call.Length > 0)
        {
            var prop = call.Realm.ToJsString(call[0]);
            if (TryGetStylePropertyRawValue(styleMap, prop, out var val))
                return JsValue.String(CssPriority.Strip(val));
            var camel = CssPropertyNames.ToDomPropertyName(prop);
            if (TryReadFromReceiver(in call, camel, out var jsVal) ||
                TryReadFromReceiver(in call, prop, out jsVal))
            {
                return jsVal;
            }
        }

        return JsValue.String(string.Empty);
    }

    private static JsValue RuleRemoveProperty(Dictionary<string, string> styleMap, in JsCall call)
    {
        if (call.Length > 0)
        {
            var prop = call.Realm.ToJsString(call[0]);
            var removed = TryGetStylePropertyRawValue(styleMap, prop, out var val) ? CssPriority.Strip(val) : string.Empty;
            styleMap.Remove(prop);
            styleMap.Remove(ToCssPropertyName(prop));
            return JsValue.String(removed);
        }

        return JsValue.String(string.Empty);
    }

    private static JsValue RuleGetCssFloat(Dictionary<string, string> styleMap)
    {
        if (styleMap.TryGetValue("float", out var val))
            return JsValue.String(val);
        return JsValue.String(string.Empty);
    }

    private static JsValue RuleSetCssFloat(Dictionary<string, string> styleMap, in JsCall call)
    {
        if (call.Length > 0)
        {
            var val = call.Realm.ToJsString(call[0]);
            if (string.IsNullOrEmpty(val) || DomBridge.IsAcceptableInlineValue("float", val))
                styleMap["float"] = val;
        }
        return JsValue.Undefined;
    }

    private static JsValue RuleItem(Dictionary<string, string> styleMap, in JsCall call)
    {
        if (call.Length > 0 && int.TryParse(call.Realm.ToJsString(call[0]), out var index))
        {
            var propertyNames = GetStylePropertyNames(styleMap);
            if (index >= 0 && index < propertyNames.Count)
                return JsValue.String(propertyNames[index]);
        }

        return JsValue.String(string.Empty);
    }

    private static JsValue RuleGetPropertyPriority(Dictionary<string, string> styleMap, in JsCall call)
    {
        if (call.Length > 0 &&
            TryGetStylePropertyRawValue(styleMap, call.Realm.ToJsString(call[0]), out var value))
        {
            return JsValue.String(CssPriority.Parse(value));
        }

        return JsValue.String(string.Empty);
    }

    // -------- getComputedStyle (read-only, engine-produced map) --------

    private static JsValue ComputedGetPropertyValue(Dictionary<string, string> computed, in JsCall call)
    {
        if (call.Length > 0)
        {
            var name = call.Realm.ToJsString(call[0]);
            if (computed.TryGetValue(name, out var val))
                return JsValue.String(CssPriority.Strip(val));

            // Try kebab-case conversion for camelCase input
            var kebab = ToCssPropertyName(name);
            if (kebab != name && computed.TryGetValue(kebab, out val))
                return JsValue.String(CssPriority.Strip(val));

            // Try camelCase conversion for kebab-case input
            var camel = CssPropertyNames.ToDomPropertyName(name);
            if (camel != name && computed.TryGetValue(camel, out val))
                return JsValue.String(CssPriority.Strip(val));
        }

        return JsValue.String(string.Empty);
    }

    private static JsValue ComputedItem(List<string> propertyNames, in JsCall call)
    {
        if (call.Length > 0 && int.TryParse(call.Realm.ToJsString(call[0]), out var index))
        {
            if (index >= 0 && index < propertyNames.Count)
                return JsValue.String(propertyNames[index]);
        }

        return JsValue.String(string.Empty);
    }
}
