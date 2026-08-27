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
/// The CSSOM style-sheet / CSS-rule <b>object model</b> feature binding (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.15) — the sibling of <see cref="StyleDeclarationBinding"/>
/// (P3.14). It builds the JS <c>CSSRuleList</c>, the per-rule <c>CSSRule</c> objects (every at-rule
/// kind plus style/keyframe rules) and their <c>cssText</c>/<c>insertRule</c>/<c>deleteRule</c>
/// callbacks from the neutral <see cref="Broiler.CSS.CssRule"/> model and the
/// <see cref="Broiler.CSS.Cssom.CssomRuleMetadata"/> projection.
/// <para>
/// Like <see cref="StyleDeclarationBinding"/> it is an <b>internal static class with no host
/// contract</b>: pure CSSOM-IDL logic over the shared rule model, the canonical
/// <c>CssSerializer</c>/<c>CssParser</c>, and <see cref="StyleDeclarationBinding.BuildRuleDeclaration"/>
/// for a rule's <c>style</c>; the one bridge helper it needs is the neutral static
/// <c>DomBridge.ParseStyle</c>. The <em>CSSStyleSheet</em> object itself — its per-element identity
/// cache, the live <c>cssRules</c> collection and the insert/delete mutation bookkeeping that marks the
/// shared model mutated — stays bridge-owned in <c>DomBridge.BuildStyleSheetObject</c> (runtime-state
/// coupled), which calls into this module for the rule objects.
/// </para>
/// This file holds the rule-list and keyframe-rule builders; the per-rule <c>CSSRule</c> builder is in
/// <c>StyleSheetBinding.Rules.cs</c> and the <c>JsStyleSheets*Core</c> callbacks in
/// <c>StyleSheetBinding.Callbacks.cs</c>.
/// </summary>
internal static partial class StyleSheetBinding
{
    /// <summary>Parses CSS text into individual rule strings.</summary>
    private static List<string> ParseCssRuleStrings(string cssText)
    {
        return [.. new CssParser().ParseStyleSheet(cssText)
            .Rules.Select(CssSerializer.Serialize)];
    }

    private static JSObject BuildCssRuleListObject(List<JSObject> rules, Func<string, JSObject>? ruleFactory = null)
    {
        var cssRuleList = new JSObject();
        var lastSyncedCount = 0;

        void SyncIndices()
        {
            for (var i = 0; i < rules.Count; i++)
                cssRuleList[(uint)i] = rules[i];

            for (var i = rules.Count; i < lastSyncedCount; i++)
                cssRuleList.GetElements().RemoveAt((uint)i);

            lastSyncedCount = rules.Count;
        }

        SyncIndices();

        cssRuleList.FastAddProperty("length",
            new DomFunction((in _) => new JSNumber(rules.Count), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        cssRuleList.FastAddValue("item",
            new DomFunction((in a) => JsStyleSheetsItem008Core(rules, in a), "item", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        cssRuleList.FastAddValue("insertRule",
            new DomFunction((in a) => JsStyleSheetsInsertRule009Core(SyncIndices, ruleFactory, rules, in a), "insertRule", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        cssRuleList.FastAddValue("deleteRule",
            new DomFunction((in a) => JsStyleSheetsDeleteRule010Core(SyncIndices, rules, in a), "deleteRule", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return cssRuleList;
    }

    /// <summary>
    /// Builds the nested rule objects for an at-rule wrapper — from the model
    /// (<paramref name="nestedModelRules"/>) when available, otherwise by parsing
    /// <paramref name="nestedCss"/>. The model path avoids the serialize→reparse
    /// round-trip for initial construction (Phase 6); nested <c>insertRule</c> still
    /// feeds strings through the list factory, which is correct (JS supplies text).
    /// </summary>
    private static List<JSObject> BuildNestedRuleObjects(string nestedCss,
        IReadOnlyList<CssRule>? nestedModelRules,
        JSObject parentStyleSheet,
        JSObject parentRule) =>
        [.. (nestedModelRules is not null
            ? nestedModelRules.Select(rule => BuildCssRuleObject(rule, parentStyleSheet, parentRule))
            : ParseCssRuleStrings(nestedCss).Select(rule => BuildCssRuleObject(rule, parentStyleSheet, parentRule)))];

    /// <summary>Keyframe-rule variant of <see cref="BuildNestedRuleObjects"/>.</summary>
    private static List<JSObject> BuildNestedKeyframeObjects(
        string nestedCss,
        IReadOnlyList<CssRule>? nestedModelRules,
        JSObject parentStyleSheet,
        JSObject parentRule) =>
        [.. (nestedModelRules is not null
            ? nestedModelRules.Select(rule => BuildCssKeyframeRuleObject(rule, parentStyleSheet, parentRule))
            : ParseCssRuleStrings(nestedCss).Select(rule => BuildCssKeyframeRuleObject(rule, parentStyleSheet, parentRule)))];

    private static JSObject BuildCssKeyframeRuleObject(CssRule rule, JSObject parentStyleSheet, JSObject parentRule)
    {
        // A keyframe block is a style rule whose selector is the key text
        // (e.g. "0%, 50%"). Read the key + declarations from the model instead of
        // serializing and re-parsing; unexpected shapes fall back to the text path.
        if (rule is not CssStyleRule styleRule)
            return BuildCssKeyframeRuleObject(CssSerializer.Serialize(rule), parentStyleSheet, parentRule);

        var ruleObj = new JSObject();
        ruleObj.FastAddProperty("parentStyleSheet",
            new DomFunction((in _) => parentStyleSheet, "get parentStyleSheet"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        ruleObj.FastAddProperty("parentRule",
            new DomFunction((in _) => parentRule, "get parentRule"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        ruleObj.FastAddValue("type", new JSNumber(8), JSPropertyAttributes.EnumerableConfigurableValue);

        var keyText = CssomRuleMetadata.GetSelectorText(styleRule);
        ruleObj.FastAddValue("keyText", new JSString(keyText), JSPropertyAttributes.EnumerableConfigurableValue);
        ruleObj.FastAddProperty("cssText",
            new DomFunction((in _) => JsStyleSheetsGetCssText013Core(keyText, ruleObj, in _), "get cssText"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(DomBridge.ParseStyle(CssSerializer.Serialize(styleRule.Declarations)), ruleObj);
        ruleObj.FastAddValue("style", styleObj, JSPropertyAttributes.EnumerableConfigurableValue);

        return ruleObj;
    }

    private static JSObject BuildCssKeyframeRuleObject(string ruleText, JSObject parentStyleSheet, JSObject parentRule)
    {
        var ruleObj = new JSObject();
        ruleObj.FastAddProperty("parentStyleSheet",
            new DomFunction((in _) => parentStyleSheet, "get parentStyleSheet"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        ruleObj.FastAddProperty("parentRule",
            new DomFunction((in _) => parentRule, "get parentRule"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        ruleObj.FastAddValue("type", new JSNumber(8), JSPropertyAttributes.EnumerableConfigurableValue);

        int braceOpen = ruleText.IndexOf('{');
        if (braceOpen >= 0)
        {
            var keyText = ruleText[..braceOpen].Trim();
            ruleObj.FastAddValue("keyText", new JSString(keyText), JSPropertyAttributes.EnumerableConfigurableValue);
            ruleObj.FastAddProperty("cssText",
                new DomFunction((in _) => JsStyleSheetsGetCssText013Core(keyText, ruleObj, in _), "get cssText"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);

            int braceClose = ruleText.LastIndexOf('}');
            if (braceClose > braceOpen)
            {
                var declarations = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var styleMap = DomBridge.ParseStyle(declarations);
                var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(styleMap, ruleObj);
                ruleObj.FastAddValue("style", styleObj, JSPropertyAttributes.EnumerableConfigurableValue);
            }
        }

        return ruleObj;
    }
}
