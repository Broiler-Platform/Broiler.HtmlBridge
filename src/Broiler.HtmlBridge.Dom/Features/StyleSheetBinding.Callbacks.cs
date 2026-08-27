using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;
using Broiler.CSS;
using Broiler.CSS.Cssom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>JsStyleSheets*Core</c> callback half of <see cref="StyleSheetBinding"/> (Phase 3, P3.15):
/// the <c>CSSStyleSheet</c>/<c>CSSRuleList</c> <c>length</c>/<c>item</c>/<c>cssRules</c>/<c>insertRule</c>/
/// <c>deleteRule</c> operations (driven by closures the bridge's <c>BuildStyleSheetObject</c> supplies)
/// and the per-rule-kind <c>cssText</c> serializers. Pure functions over their arguments — no state.
/// </summary>
internal static partial class StyleSheetBinding
{
    internal static JSValue JsStyleSheetsGetLength002Core(Func<List<CssRule>> currentRules, in Arguments _) => new JSNumber(currentRules().Count);


    internal static JSValue JsStyleSheetsItem003Core(Action syncLiveCssRulesIndices, JSObject? liveCssRules, Func<List<CssRule>> currentRules, in Arguments a)
    {
        syncLiveCssRulesIndices();
        var dv = a.Length > 0 ? a[0].DoubleValue : 0;
        var idx = double.IsNaN(dv) ? 0 : (int)dv;
        return idx >= 0 && idx < currentRules().Count ? liveCssRules[(uint)idx] : JSNull.Value;
    }


    internal static JSValue JsStyleSheetsGetCssRules004Core(Action syncLiveCssRulesIndices, JSObject? liveCssRules, in Arguments _)
    {
        syncLiveCssRulesIndices();
        return liveCssRules;
    }


    internal static JSValue JsStyleSheetsInsertRule005Core(Func<List<CssRule>> currentRules, Action markRulesMutated, Action syncLiveCssRulesIndices, in Arguments a)
    {
        var ruleText = a.Length > 0 ? a[0].ToString() : string.Empty;
        // currentRules() reparses on any pending textContent change before we mutate,
        // so the index is clamped against the up-to-date shared model.
        var rules = currentRules();
        var dv = a.Length > 1 ? a[1].DoubleValue : rules.Count;
        var index = double.IsNaN(dv) ? rules.Count : (int)dv;
        index = Math.Clamp(index, 0, rules.Count);
        // Route the mutation through the shared model: parse the inserted text
        // into a CssRule rather than storing the raw string (Phase 6).
        var parsed = new CssParser().ParseStyleSheet(ruleText).Rules;
        if (parsed.Count > 0)
        {
            rules.Insert(index, parsed[0]);
            markRulesMutated();
        }

        syncLiveCssRulesIndices();
        return new JSNumber(index);
    }


    internal static JSValue JsStyleSheetsDeleteRule006Core(Func<List<CssRule>> currentRules, Action markRulesMutated, Action syncLiveCssRulesIndices, in Arguments a)
    {
        var rules = currentRules();
        if (a.Length > 0)
        {
            var dv = a[0].DoubleValue;
            var idx = double.IsNaN(dv) ? 0 : (int)dv;
            if (idx >= 0 && idx < rules.Count)
            {
                rules.RemoveAt(idx);
                markRulesMutated();
            }

            syncLiveCssRulesIndices();
        }

        return JSUndefined.Value;
    }


    private static JSValue JsStyleSheetsItem008Core(List<JSObject> rules, in Arguments a)
    {
        var dv = a.Length > 0 ? a[0].DoubleValue : 0;
        var index = double.IsNaN(dv) ? 0 : (int)dv;
        return index >= 0 && index < rules.Count ? rules[index] : JSNull.Value;
    }


    private static JSValue JsStyleSheetsInsertRule009Core(Action syncIndices, Func<string, JSObject>? ruleFactory, List<JSObject> rules, in Arguments a)
    {
        if (ruleFactory is null)
            return new JSNumber(0);
        var ruleText = a.Length > 0 ? a[0].ToString() : string.Empty;
        var dv = a.Length > 1 ? a[1].DoubleValue : rules.Count;
        var index = double.IsNaN(dv) ? rules.Count : (int)dv;
        index = Math.Clamp(index, 0, rules.Count);
        rules.Insert(index, ruleFactory(ruleText));
        syncIndices();
        return new JSNumber(index);
    }


    private static JSValue JsStyleSheetsDeleteRule010Core(Action syncIndices, List<JSObject> rules, in Arguments a)
    {
        var dv = a.Length > 0 ? a[0].DoubleValue : 0;
        var index = double.IsNaN(dv) ? 0 : (int)dv;
        if (index >= 0 && index < rules.Count)
        {
            rules.RemoveAt(index);
            syncIndices();
        }

        return JSUndefined.Value;
    }

    private static JSValue JsStyleSheetsGetCssText013Core(string? keyText, JSObject? ruleObj, in Arguments _)
    {
        var styleObj = ruleObj[(KeyString)"style"];
        var styleText = styleObj?[(KeyString)"cssText"]?.ToString() ?? string.Empty;
        return new JSString($"{keyText} {{ {styleText} }}");
    }


    private static JSValue JsStyleSheetsGetCssText017Core(string? href, string? mediaText, in Arguments _)
    {
        var mediaSuffix = string.IsNullOrEmpty(mediaText) ? string.Empty : $" {mediaText}";
        return new JSString($"@import url(\"{href}\"){mediaSuffix};");
    }


    private static JSValue JsStyleSheetsGetCssText018Core(string? mediaText, List<JSObject>? nestedRuleObjects, in Arguments _)
    {
        var nestedCssText = string.Join(" ", nestedRuleObjects.Select(rule => rule[(KeyString)"cssText"]?.ToString()).Where(text => !string.IsNullOrEmpty(text)));
        return new JSString($"@media {mediaText} {{ {nestedCssText} }}");
    }


    private static JSValue JsStyleSheetsGetCssText019Core(JSObject? styleObj, in Arguments _)
    {
        var cssText = styleObj[(KeyString)"cssText"]?.ToString() ?? string.Empty;
        return new JSString($"@font-face {{ {cssText} }}");
    }


    private static JSValue JsStyleSheetsGetCssText020Core(string? name, List<JSObject>? nestedRuleObjects, in Arguments _)
    {
        var nestedCssText = string.Join(" ", nestedRuleObjects.Select(rule => rule[(KeyString)"cssText"]?.ToString()).Where(text => !string.IsNullOrEmpty(text)));
        return new JSString($"@keyframes {name} {{ {nestedCssText} }}");
    }


    private static JSValue JsStyleSheetsGetCssText021Core(bool inherits, string? initialValue, string? propertyName, string? syntax, in Arguments _)
    {
        var serialized = new List<string>
                        {
                            $"syntax: \"{CssomRuleMetadata.EscapeDescriptorString(syntax)}\"",
                            $"inherits: {(inherits ? "true" : "false")}"};
        if (!string.IsNullOrEmpty(initialValue))
            serialized.Add($"initial-value: {initialValue}");
        return new JSString($"@property {propertyName} {{ {string.Join("; ", serialized)}; }}");
    }


    private static JSValue JsStyleSheetsGetCssText022Core((string CssName, string JsName)[]? descriptorMap, string? ruleName, JSObject? ruleObj, in Arguments _)
    {
        var serialized = new List<string>();
        foreach (var (cssName, jsName) in descriptorMap)
        {
            var value = ruleObj[(KeyString)jsName];
            if (value is null || value == JSUndefined.Value)
                continue;
            var text = value.ToString();
            if (!string.IsNullOrEmpty(text))
                serialized.Add($"{cssName}: {text}");
        }

        return new JSString($"@counter-style {ruleName} {{ {string.Join("; ", serialized)}; }}");
    }


    private static JSValue JsStyleSheetsGetCssText023Core(string? conditionText, List<JSObject>? nestedRuleObjects, in Arguments _)
    {
        var nestedCssText = string.Join(" ", nestedRuleObjects.Select(rule => rule[(KeyString)"cssText"]?.ToString()).Where(text => !string.IsNullOrEmpty(text)));
        return new JSString($"@supports {conditionText} {{ {nestedCssText} }}");
    }


    private static JSValue JsStyleSheetsGetCssText024Core(string? nameText, List<JSObject>? nestedRuleObjects, in Arguments _)
    {
        var nestedCssText = string.Join(" ", nestedRuleObjects.Select(rule => rule[(KeyString)"cssText"]?.ToString()).Where(text => !string.IsNullOrEmpty(text)));
        var namePrefix = string.IsNullOrEmpty(nameText) ? string.Empty : $"{nameText} ";
        return new JSString($"@layer {namePrefix}{{ {nestedCssText} }}");
    }


    private static JSValue JsStyleSheetsGetCssText025Core(string? nameText, in Arguments _)
    {
        var nameSuffix = string.IsNullOrEmpty(nameText) ? string.Empty : $" {nameText}";
        return new JSString($"@layer{nameSuffix};");
    }


    private static JSValue JsStyleSheetsGetCssText026Core(string? namespaceUri, string? prefix, in Arguments _)
    {
        var prefixPart = string.IsNullOrEmpty(prefix) ? string.Empty : $"{prefix} ";
        return new JSString($"@namespace {prefixPart}\"{namespaceUri}\";");
    }


    private static JSValue JsStyleSheetsGetCssText027Core(string? selectorText, JSObject? styleObj, in Arguments _)
    {
        var styleText = styleObj[(KeyString)"cssText"]?.ToString() ?? string.Empty;
        var selectorSuffix = string.IsNullOrEmpty(selectorText) ? string.Empty : $" {selectorText}";
        return new JSString($"@page{selectorSuffix} {{ {styleText} }}");
    }


    private static JSValue JsStyleSheetsGetCssText028Core(JSObject? ruleObj, string? selectorText, in Arguments _)
    {
        var styleObj = ruleObj[(KeyString)"style"];
        var styleText = styleObj?[(KeyString)"cssText"]?.ToString() ?? string.Empty;
        return new JSString($"{selectorText} {{ {styleText} }}");
    }
}
