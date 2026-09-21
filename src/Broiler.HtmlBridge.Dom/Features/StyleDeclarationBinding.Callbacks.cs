using Broiler.CSS;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <see cref="StyleDeclarationBinding"/> — the per-method CSSSStyleDeclaration callbacks, in three
/// families: <c>Inline*</c> (the writable <c>element.style</c>, over the inline-style store),
/// <c>Rule*</c> (the writable rule declaration, over a <see cref="RuleDeclarationStore"/>) and <c>Computed*</c> (the read-only
/// getComputedStyle result).
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's: each callback reads its arguments off the
/// <see cref="JsCall"/> frame and coerces them through the frame's realm, so a string argument runs
/// the ECMAScript <c>ToString</c>/<c>ToNumber</c> a page observes. A callback that reads no argument
/// takes no frame at all.
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
            foreach (var kv in DomBridgeUtils.ParseStyle(call.Realm.ToJsString(call[0]), reportDrops: true))
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
        foreach (var kv in DomBridgeUtils.ParseStyle(cssText, reportDrops: true))
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
            else if (DomBridgeUtils.IsAcceptableInlineValue(prop, value))
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
            if (string.IsNullOrEmpty(val) || DomBridgeUtils.IsAcceptableInlineValue("float", val))
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

    // -------- rule.style (writable, over a RuleDeclarationStore) --------

    private static JsValue RuleGetCssText(RuleDeclarationStore store)
    {
        var parts = store.Declared.Select(kv => $"{kv.Key}: {kv.Value}");
        var text = string.Join("; ", parts);
        return JsValue.String(text.Length > 0 ? text + ";" : text);
    }

    private static JsValue RuleSetCssText(RuleDeclarationStore store, in JsCall call)
    {
        store.ReplaceWith(call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }

    /// <summary>
    /// CSSOM <c>setProperty</c>: the name is looked up ASCII-lowercased
    /// (<see cref="RuleDeclarationEdits.CssomPropertyName"/>), and the call returns without an edit unless the
    /// priority is empty or an ASCII case-insensitive <c>important</c> and the value parses for the property.
    /// </summary>
    /// <remarks>
    /// The two early returns are what a detached map could skip and a store that writes through cannot:
    /// <c>CssPriority.Apply</c> reads any other priority as none and strips an <c>!important</c> written into
    /// the value, so <c>setProperty('color', 'red', 'bogus')</c> and <c>setProperty('color', 'red !important')</c>
    /// each wrote a normal <c>red</c> into the sheet — over an author's <c>!important</c> one, too — where CSSOM
    /// changes nothing. The priority is compared untrimmed and ASCII case-insensitively, as CSSOM compares it.
    /// </remarks>
    private static JsValue RuleSetProperty(RuleDeclarationStore store, in JsCall call)
    {
        if (call.Length >= 2)
        {
            var prop = RuleDeclarationEdits.CssomPropertyName(call.Realm.ToJsString(call[0]));
            var raw = call.Realm.ToJsString(call[1]);
            var priority = call.Length >= 3 ? call.Realm.ToJsString(call[2]) : string.Empty;
            if (priority.Length > 0 && !System.Text.Ascii.EqualsIgnoreCase(priority, "important"))
                return JsValue.Undefined;

            if (string.IsNullOrEmpty(raw))
                store.Remove(prop);
            else if (IsRuleValue(prop, raw))
                store.Set(prop, CssPriority.Apply(raw, priority));
            // setProperty with an invalid value is a no-op per CSSOM.
        }

        return JsValue.Undefined;
    }

    /// <remarks>
    /// Answers from the store alone, with no fallback to the declaration object's own properties: a
    /// camel-cased attribute write leaves no copy there (<see cref="RuleDeclaration.TrySetNamed"/>),
    /// so such a fallback could only answer a stale copy — or, for
    /// <c>getPropertyValue('cssText')</c>, the declaration's own <c>cssText</c>.
    /// </remarks>
    private static JsValue RuleGetPropertyValue(RuleDeclarationStore store, in JsCall call)
    {
        if (call.Length > 0 &&
            TryGetStylePropertyRawValue(store.Declared, call.Realm.ToJsString(call[0]), out var val))
        {
            return JsValue.String(CssPriority.Strip(val));
        }

        return JsValue.String(string.Empty);
    }

    /// <remarks>
    /// Removes the one name CSSOM looks up, and answers that name's value. The camel-cased spelling
    /// (<c>removeProperty('marginTop')</c>) used to remove the kebab-cased property too — harmless on a
    /// detached map, but through the store it removed <c>margin-top</c> from the sheet, splitting an author's
    /// <c>margin</c>, where CSSOM looks up <c>margintop</c> and removes nothing; and <c>--myVar</c> removed
    /// <c>--my-var</c> as well, a different custom property.
    /// </remarks>
    private static JsValue RuleRemoveProperty(RuleDeclarationStore store, in JsCall call)
    {
        if (call.Length > 0)
        {
            var name = RuleDeclarationEdits.CssomPropertyName(call.Realm.ToJsString(call[0]));
            var removed = store.Declared.TryGetValue(name, out var val) ? CssPriority.Strip(val) : string.Empty;
            store.Remove(name);
            return JsValue.String(removed);
        }

        return JsValue.String(string.Empty);
    }

    private static JsValue RuleGetCssFloat(RuleDeclarationStore store)
    {
        if (store.Declared.TryGetValue("float", out var val))
            return JsValue.String(val);
        return JsValue.String(string.Empty);
    }

    private static JsValue RuleSetCssFloat(RuleDeclarationStore store, in JsCall call)
    {
        if (call.Length > 0)
        {
            // CSSOM: cssFloat is setProperty('float', value), so an empty value removes the declaration
            // rather than storing an empty one.
            var val = call.Realm.ToJsString(call[0]);
            if (string.IsNullOrEmpty(val))
                store.Remove("float");
            else if (IsRuleValue("float", val))
                store.Set("float", val);
        }
        return JsValue.Undefined;
    }

    private static JsValue RuleItem(RuleDeclarationStore store, in JsCall call)
    {
        if (call.Length > 0 && int.TryParse(call.Realm.ToJsString(call[0]), out var index))
        {
            var propertyNames = GetStylePropertyNames(store.Declared);
            if (index >= 0 && index < propertyNames.Count)
                return JsValue.String(propertyNames[index]);
        }

        return JsValue.String(string.Empty);
    }

    private static JsValue RuleGetPropertyPriority(RuleDeclarationStore store, in JsCall call)
    {
        if (call.Length > 0 &&
            TryGetStylePropertyRawValue(store.Declared, call.Realm.ToJsString(call[0]), out var value))
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
