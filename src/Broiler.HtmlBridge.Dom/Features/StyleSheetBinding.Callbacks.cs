using Broiler.CSS;
using Broiler.CSS.Cssom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>JsStyleSheets*Core</c> callback half of <see cref="StyleSheetBinding"/> (Phase 3, P3.15):
/// the <c>CSSStyleSheet</c>/<c>CSSRuleList</c> <c>length</c>/<c>item</c>/<c>cssRules</c>/<c>insertRule</c>/
/// <c>deleteRule</c> operations (driven by closures the bridge's <c>BuildStyleSheetObject</c> supplies),
/// the one parse of the text a page inserts (<see cref="ParseSingleRule"/>) and the per-rule-kind
/// <c>cssText</c> serializers. Pure functions over their arguments — no state.
/// </summary>
/// <remarks>
/// An index argument goes through <see cref="IJsValues.ToNumber"/> rather than reading the handle,
/// because <c>deleteRule("0")</c> is a call a page makes and the string has to coerce the way the
/// language says. A sheet-level index is an index into the CSSOM view of the shared model
/// (<see cref="CssomRules"/>), never a model position. The <c>cssText</c> serializers read a nested
/// object's <c>cssText</c> through <see cref="CssTextOf"/>, which keeps the null-tolerance the
/// engine-typed originals had.
/// </remarks>
internal static partial class StyleSheetBinding
{
    /// <summary>
    /// The index argument at <paramref name="position"/>, or <paramref name="whenAbsent"/> when the call
    /// did not supply one — with NaN folded to zero exactly as the engine-typed originals did.
    /// </summary>
    private static int IndexArgument(in JsCall call, int position, int whenAbsent)
    {
        if (call.Length <= position)
            return whenAbsent;

        // ToNumber, not the handle's AsNumber: a page may pass "1", and the observable coercion is the
        // engine's. NaN was folded rather than rejected before, and a rejected index is a different
        // outcome from index zero, so the fold stays.
        var value = call.Realm.ToNumber(call[position]);
        return double.IsNaN(value) ? whenAbsent : (int)value;
    }

    /// <summary>
    /// <paramref name="target"/>'s <c>cssText</c> as a string — the migrated form of the
    /// doubly-null-conditional property read the engine-typed originals used, falling back to the empty
    /// string.
    /// </summary>
    /// <remarks>
    /// <see cref="JsValue.Missing"/> stands where the engine handed back a CLR <see langword="null"/>, so
    /// both null-conditionals become an <c>IsMissing</c> test and an absent nested object still answers
    /// the empty string rather than the word "undefined".
    /// </remarks>
    private static string CssTextOf(IJsRealm realm, JsValue target)
    {
        if (target.IsMissing)
            return string.Empty;

        var text = realm.GetProperty(target, "cssText");
        return text.IsMissing ? string.Empty : realm.ToJsString(text);
    }

    /// <summary>The joined <c>cssText</c> of an at-rule's nested rule objects, empties dropped.</summary>
    private static string NestedCssTextOf(IJsRealm realm, List<JsValue> nestedRuleObjects) =>
        string.Join(" ", nestedRuleObjects
            .Select(rule => CssTextOf(realm, rule))
            .Where(text => !string.IsNullOrEmpty(text)));

    /// <summary>
    /// Parses the text a page passes to <c>insertRule</c> as exactly one rule — CSSOM "parse a CSS rule" —
    /// and throws the <c>SyntaxError</c> DOMException when it is not one.
    /// </summary>
    /// <remarks>
    /// The consumed parser answers a rule list and diagnostics rather than one rule or a failure, so
    /// "exactly one" is read off source ranges. It flattens CSS Nesting: <c>.e { .f {} }</c> parses into
    /// <c>.e</c> followed by <c>.e .f</c>, and a nested at-rule likewise, so a later rule whose range lies
    /// inside the first rule's is part of that rule, and one outside it is a second rule of the text. The
    /// same test applies to Error diagnostics: text left after the rule (<c>.b {} junk</c>) is reported
    /// outside it, while a block cut off by the end of the text (CSS1002–CSS1004) is reported over the rule
    /// itself, which CSS Syntax still accepts as a rule, as Chromium does. A dropped declaration is a
    /// Warning and never rejects the rule. Whether the one rule is one this list accepts is the caller's
    /// question.
    /// </remarks>
    private static CssRule ParseSingleRule(IJsRealm realm, string ruleText)
    {
        var parsed = new CssParser().ParseStyleSheet(ruleText);
        if (parsed.Rules.Count > 0)
        {
            var extent = RangeOf(parsed.Rules[0]);
            if (parsed.Rules.Skip(1).All(rule => Encloses(extent, RangeOf(rule))) &&
                parsed.Diagnostics.All(diagnostic =>
                    diagnostic.Severity != CssDiagnosticSeverity.Error || Encloses(extent, diagnostic.Range)))
                return parsed.Rules[0];
        }

        throw SyntaxError(realm, ruleText);
    }

    /// <summary>The <c>SyntaxError</c> an insert of <paramref name="ruleText"/> throws.</summary>
    private static Exception SyntaxError(IJsRealm realm, string ruleText) =>
        realm.DomError("SyntaxError", $"Failed to parse the rule '{ruleText}'.");

    private static CssSourceRange RangeOf(CssRule rule) => rule switch
    {
        CssStyleRule styleRule => styleRule.Range,
        CssAtRule atRule => atRule.Range,
        _ => default,
    };

    private static bool Encloses(CssSourceRange outer, CssSourceRange inner) =>
        inner.Start >= outer.Start && inner.Start + inner.Length <= outer.Start + outer.Length;

    internal static JsValue JsStyleSheetsGetLength002Core(Func<List<CssRule>> currentRules) =>
        JsValue.Number(CssomRuleCount(currentRules()));


    internal static JsValue JsStyleSheetsItem003Core(Action syncLiveCssRulesIndices, JsValue liveCssRules, Func<List<CssRule>> currentRules, in JsCall call)
    {
        syncLiveCssRulesIndices();
        var idx = IndexArgument(in call, 0, 0);
        return idx >= 0 && idx < CssomRuleCount(currentRules())
            ? call.Realm.GetIndex(liveCssRules, (uint)idx)
            : JsValue.Null;
    }


    internal static JsValue JsStyleSheetsGetCssRules004Core(Action syncLiveCssRulesIndices, JsValue liveCssRules)
    {
        syncLiveCssRulesIndices();
        return liveCssRules;
    }


    internal static JsValue JsStyleSheetsInsertRule005Core(Func<List<CssRule>> currentRules, Action markRulesMutated, Action syncLiveCssRulesIndices, in JsCall call)
    {
        var ruleText = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        // currentRules() reparses on any pending textContent change before we mutate,
        // so the index is clamped against the up-to-date shared model — counted as the page
        // counts it, over the rules the CSSOM shows.
        var rules = currentRules();
        var count = CssomRuleCount(rules);
        var index = Math.Clamp(IndexArgument(in call, 1, count), 0, count);
        // Route the mutation through the shared model: the inserted text becomes exactly one parsed
        // CssRule rather than a stored string (Phase 6), and text that is not one rule the CSSOM shows is
        // the SyntaxError Chromium throws rather than an insert of nothing or of its first rule.
        var rule = ParseSingleRule(call.Realm, ruleText);
        if (!IsCssomVisible(rule))
            throw SyntaxError(call.Realm, ruleText);

        // Just before the visible rule now at the index, so a hidden rule ahead of it keeps its place.
        rules.Insert(ModelIndexOf(rules, index), rule);
        markRulesMutated();
        syncLiveCssRulesIndices();
        return JsValue.Number(index);
    }


    internal static JsValue JsStyleSheetsDeleteRule006Core(Func<List<CssRule>> currentRules, Action markRulesMutated, Action syncLiveCssRulesIndices, in JsCall call)
    {
        var rules = currentRules();
        if (call.Length > 0)
        {
            var idx = IndexArgument(in call, 0, 0);
            if (idx >= 0 && idx < CssomRuleCount(rules))
            {
                rules.RemoveAt(ModelIndexOf(rules, idx));
                markRulesMutated();
            }

            syncLiveCssRulesIndices();
        }

        return JsValue.Undefined;
    }


    private static JsValue JsStyleSheetsItem008Core(List<JsValue> rules, in JsCall call)
    {
        var index = IndexArgument(in call, 0, 0);
        return index >= 0 && index < rules.Count ? rules[index] : JsValue.Null;
    }


    private static JsValue JsStyleSheetsInsertRule009Core(Action syncIndices, Func<CssRule, JsValue>? ruleFactory, List<JsValue> rules, in JsCall call)
    {
        if (ruleFactory is null)
            return JsValue.Number(0);
        var ruleText = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        var index = Math.Clamp(IndexArgument(in call, 1, rules.Count), 0, rules.Count);
        // Parsed once, as a sheet-level insert parses; the factory answers Missing for a rule this list
        // does not hold (an unsupported at-rule, or anything but a keyframe in a @keyframes list).
        var built = ruleFactory(ParseSingleRule(call.Realm, ruleText));
        if (built.IsMissing)
            throw SyntaxError(call.Realm, ruleText);

        rules.Insert(index, built);
        syncIndices();
        return JsValue.Number(index);
    }


    private static JsValue JsStyleSheetsDeleteRule010Core(Action syncIndices, List<JsValue> rules, in JsCall call)
    {
        var index = IndexArgument(in call, 0, 0);
        if (index >= 0 && index < rules.Count)
        {
            rules.RemoveAt(index);
            syncIndices();
        }

        return JsValue.Undefined;
    }

    private static JsValue JsStyleSheetsGetCssText013Core(IJsRealm realm, string? keyText, JsValue ruleObj)
    {
        var styleObj = realm.GetProperty(ruleObj, "style");
        var styleText = CssTextOf(realm, styleObj);
        return JsValue.String($"{keyText} {{ {styleText} }}");
    }


    private static JsValue JsStyleSheetsGetCssText017Core(string? href, string? mediaText)
    {
        var mediaSuffix = string.IsNullOrEmpty(mediaText) ? string.Empty : $" {mediaText}";
        return JsValue.String($"@import url(\"{href}\"){mediaSuffix};");
    }


    private static JsValue JsStyleSheetsGetCssText018Core(IJsRealm realm, string? mediaText, List<JsValue> nestedRuleObjects) =>
        JsValue.String($"@media {mediaText} {{ {NestedCssTextOf(realm, nestedRuleObjects)} }}");


    private static JsValue JsStyleSheetsGetCssText019Core(IJsRealm realm, JsValue styleObj) =>
        JsValue.String($"@font-face {{ {CssTextOf(realm, styleObj)} }}");


    private static JsValue JsStyleSheetsGetCssText020Core(IJsRealm realm, string keyword, string? name, List<JsValue> nestedRuleObjects) =>
        JsValue.String($"@{keyword} {name} {{ {NestedCssTextOf(realm, nestedRuleObjects)} }}");


    private static JsValue JsStyleSheetsGetCssText021Core(bool inherits, string? initialValue, string? propertyName, string? syntax)
    {
        var serialized = new List<string>
                        {
                            $"syntax: \"{CssomRuleMetadata.EscapeDescriptorString(syntax)}\"",
                            $"inherits: {(inherits ? "true" : "false")}"};
        if (!string.IsNullOrEmpty(initialValue))
            serialized.Add($"initial-value: {initialValue}");
        return JsValue.String($"@property {propertyName} {{ {string.Join("; ", serialized)}; }}");
    }


    private static JsValue JsStyleSheetsGetCssText022Core(IJsRealm realm, (string CssName, string JsName)[] descriptorMap, string? ruleName, JsValue ruleObj)
    {
        var serialized = new List<string>();
        foreach (var (cssName, jsName) in descriptorMap)
        {
            var value = realm.GetProperty(ruleObj, jsName);
            if (value.IsMissing || value.IsUndefined)
                continue;
            var text = realm.ToJsString(value);
            if (!string.IsNullOrEmpty(text))
                serialized.Add($"{cssName}: {text}");
        }

        return JsValue.String($"@counter-style {ruleName} {{ {string.Join("; ", serialized)}; }}");
    }


    private static JsValue JsStyleSheetsGetCssText023Core(IJsRealm realm, string? conditionText, List<JsValue> nestedRuleObjects) =>
        JsValue.String($"@supports {conditionText} {{ {NestedCssTextOf(realm, nestedRuleObjects)} }}");


    private static JsValue JsStyleSheetsGetCssText024Core(IJsRealm realm, string? nameText, List<JsValue> nestedRuleObjects)
    {
        var namePrefix = string.IsNullOrEmpty(nameText) ? string.Empty : $"{nameText} ";
        return JsValue.String($"@layer {namePrefix}{{ {NestedCssTextOf(realm, nestedRuleObjects)} }}");
    }


    private static JsValue JsStyleSheetsGetCssText025Core(string? nameText)
    {
        var nameSuffix = string.IsNullOrEmpty(nameText) ? string.Empty : $" {nameText}";
        return JsValue.String($"@layer{nameSuffix};");
    }


    private static JsValue JsStyleSheetsGetCssText026Core(string? namespaceUri, string? prefix)
    {
        var prefixPart = string.IsNullOrEmpty(prefix) ? string.Empty : $"{prefix} ";
        return JsValue.String($"@namespace {prefixPart}\"{namespaceUri}\";");
    }


    private static JsValue JsStyleSheetsGetCssText027Core(IJsRealm realm, string? selectorText, JsValue styleObj)
    {
        var selectorSuffix = string.IsNullOrEmpty(selectorText) ? string.Empty : $" {selectorText}";
        return JsValue.String($"@page{selectorSuffix} {{ {CssTextOf(realm, styleObj)} }}");
    }


    private static JsValue JsStyleSheetsGetCssText028Core(IJsRealm realm, JsValue ruleObj, string? selectorText)
    {
        var styleObj = realm.GetProperty(ruleObj, "style");
        var styleText = CssTextOf(realm, styleObj);
        return JsValue.String($"{selectorText} {{ {styleText} }}");
    }


    /// <summary>The <c>cssText</c> of a grouping at-rule the metadata has no kind for (<c>@container</c> and the rest).</summary>
    private static JsValue JsStyleSheetsGetCssText029Core(IJsRealm realm, string name, string prelude, List<JsValue> nestedRuleObjects) =>
        JsValue.String($"{AtRuleHead(name, prelude)} {{ {NestedCssTextOf(realm, nestedRuleObjects)} }}");


    /// <summary>The <c>cssText</c> of an at-rule exposed with its type and text only.</summary>
    private static JsValue JsStyleSheetsGetCssText030Core(string name, string prelude, string body) =>
        JsValue.String($"{AtRuleHead(name, prelude)} {{ {body} }}");

    private static string AtRuleHead(string name, string prelude) =>
        prelude.Length == 0 ? $"@{name}" : $"@{name} {prelude}";
}
