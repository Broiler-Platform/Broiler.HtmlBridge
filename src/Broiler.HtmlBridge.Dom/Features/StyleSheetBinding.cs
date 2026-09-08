using Broiler.CSS;
using Broiler.CSS.Cssom;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so the realm is threaded through every
/// builder rather than an engine type being named — with the single exception of
/// <see cref="RetireIndex"/>, which the contract cannot express; see its remarks.
/// </remarks>
internal static partial class StyleSheetBinding
{
    /// <summary>Parses CSS text into individual rule strings.</summary>
    private static List<string> ParseCssRuleStrings(string cssText)
    {
        return [.. new CssParser().ParseStyleSheet(cssText)
            .Rules.Select(CssSerializer.Serialize)];
    }

    /// <summary>
    /// Removes the integer-indexed property <paramref name="index"/> from <paramref name="list"/>, which
    /// is how a shrinking live rule list stops offering an index whose rule was deleted.
    /// </summary>
    /// <remarks>
    /// <b>THE ONE ENGINE OPERATION LEFT IN THE CSS OBJECT MODEL, and it is a gap in the contract rather
    /// than a choice.</b> <see cref="IJsMembers"/> has <see cref="IJsMembers.DefineIndex"/> and
    /// <see cref="IJsMembers.GetIndex"/> but no <c>DeleteIndex</c>, and
    /// <see cref="IJsMembers.DeleteProperty"/> is the <em>named</em> delete: under the Broiler.JS
    /// provider it reaches the object's named property storage, while an index installed by
    /// <c>DefineIndex</c> lives in the separate element array — the same split
    /// <c>DefineIndex</c>'s own documentation calls out. So deleting "3" would answer true and leave
    /// index 3 in place.
    /// <para>
    /// The alternatives are both observable: overwriting the retired index with <c>undefined</c> leaves
    /// it present for <c>in</c> and <c>Object.keys</c>, and rebuilding the list object changes the
    /// identity of a collection the spec says is live. So the operation stays as it was, concentrated
    /// here — three callers share this one line rather than each keeping a cast of its own — and it goes
    /// away the moment JSEAL grows the member.
    /// </para>
    /// </remarks>
    internal static void RetireIndex(JsValue list, uint index) =>
        Runtime.JsInterop.ToEngineObject(list).GetElements().RemoveAt(index);

    private static JsValue BuildCssRuleListObject(IJsRealm realm, List<JsValue> rules, Func<string, JsValue>? ruleFactory = null)
    {
        var cssRuleList = realm.NewObject();
        var lastSyncedCount = 0;

        void SyncIndices()
        {
            for (var i = 0; i < rules.Count; i++)
                realm.DefineIndex(cssRuleList, (uint)i, rules[i]);

            for (var i = rules.Count; i < lastSyncedCount; i++)
                RetireIndex(cssRuleList, (uint)i);

            lastSyncedCount = rules.Count;
        }

        SyncIndices();

        realm.DefineAccessor(cssRuleList, "length", (in _) => JsValue.Number(rules.Count), null);

        realm.DefineValue(cssRuleList, "item",
            realm.NewMethod("item", (in call) => JsStyleSheetsItem008Core(rules, in call), 1));

        realm.DefineValue(cssRuleList, "insertRule",
            realm.NewMethod("insertRule", (in call) => JsStyleSheetsInsertRule009Core(SyncIndices, ruleFactory, rules, in call), 2));

        realm.DefineValue(cssRuleList, "deleteRule",
            realm.NewMethod("deleteRule", (in call) => JsStyleSheetsDeleteRule010Core(SyncIndices, rules, in call), 1));

        return cssRuleList;
    }

    /// <summary>
    /// Builds the nested rule objects for an at-rule wrapper — from the model
    /// (<paramref name="nestedModelRules"/>) when available, otherwise by parsing
    /// <paramref name="nestedCss"/>. The model path avoids the serialize→reparse
    /// round-trip for initial construction (Phase 6); nested <c>insertRule</c> still
    /// feeds strings through the list factory, which is correct (JS supplies text).
    /// </summary>
    private static List<JsValue> BuildNestedRuleObjects(IJsRealm realm, string nestedCss,
        IReadOnlyList<CssRule>? nestedModelRules,
        JsValue parentStyleSheet,
        JsValue parentRule) =>
        [.. (nestedModelRules is not null
            ? nestedModelRules.Select(rule => BuildCssRuleObject(realm, rule, parentStyleSheet, parentRule))
            : ParseCssRuleStrings(nestedCss).Select(rule => BuildCssRuleObject(realm, rule, parentStyleSheet, parentRule)))];

    /// <summary>Keyframe-rule variant of <see cref="BuildNestedRuleObjects"/>.</summary>
    private static List<JsValue> BuildNestedKeyframeObjects(
        IJsRealm realm,
        string nestedCss,
        IReadOnlyList<CssRule>? nestedModelRules,
        JsValue parentStyleSheet,
        JsValue parentRule) =>
        [.. (nestedModelRules is not null
            ? nestedModelRules.Select(rule => BuildCssKeyframeRuleObject(realm, rule, parentStyleSheet, parentRule))
            : ParseCssRuleStrings(nestedCss).Select(rule => BuildCssKeyframeRuleObject(realm, rule, parentStyleSheet, parentRule)))];

    private static JsValue BuildCssKeyframeRuleObject(IJsRealm realm, CssRule rule, JsValue parentStyleSheet, JsValue parentRule)
    {
        // A keyframe block is a style rule whose selector is the key text
        // (e.g. "0%, 50%"). Read the key + declarations from the model instead of
        // serializing and re-parsing; unexpected shapes fall back to the text path.
        if (rule is not CssStyleRule styleRule)
            return BuildCssKeyframeRuleObject(realm, CssSerializer.Serialize(rule), parentStyleSheet, parentRule);

        var ruleObj = realm.NewObject();
        realm.DefineAccessor(ruleObj, "parentStyleSheet", (in _) => parentStyleSheet, null);
        realm.DefineAccessor(ruleObj, "parentRule", (in _) => parentRule, null);

        realm.DefineValue(ruleObj, "type", JsValue.Number(8));

        var keyText = CssomRuleMetadata.GetSelectorText(styleRule);
        realm.DefineValue(ruleObj, "keyText", JsValue.String(keyText));
        realm.DefineAccessor(ruleObj, "cssText",
            (in _) => JsStyleSheetsGetCssText013Core(realm, keyText, ruleObj), null);

        var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(
            realm, DomBridge.ParseStyle(CssSerializer.Serialize(styleRule.Declarations)), ruleObj);
        realm.DefineValue(ruleObj, "style", styleObj);

        return ruleObj;
    }

    private static JsValue BuildCssKeyframeRuleObject(IJsRealm realm, string ruleText, JsValue parentStyleSheet, JsValue parentRule)
    {
        var ruleObj = realm.NewObject();
        realm.DefineAccessor(ruleObj, "parentStyleSheet", (in _) => parentStyleSheet, null);
        realm.DefineAccessor(ruleObj, "parentRule", (in _) => parentRule, null);

        realm.DefineValue(ruleObj, "type", JsValue.Number(8));

        int braceOpen = ruleText.IndexOf('{');
        if (braceOpen >= 0)
        {
            var keyText = ruleText[..braceOpen].Trim();
            realm.DefineValue(ruleObj, "keyText", JsValue.String(keyText));
            realm.DefineAccessor(ruleObj, "cssText",
                (in _) => JsStyleSheetsGetCssText013Core(realm, keyText, ruleObj), null);

            int braceClose = ruleText.LastIndexOf('}');
            if (braceClose > braceOpen)
            {
                var declarations = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var styleMap = DomBridge.ParseStyle(declarations);
                var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(realm, styleMap, ruleObj);
                realm.DefineValue(ruleObj, "style", styleObj);
            }
        }

        return ruleObj;
    }
}
