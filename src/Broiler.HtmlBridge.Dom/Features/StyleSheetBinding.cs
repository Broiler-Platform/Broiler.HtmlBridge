using Broiler.CSS;
using Broiler.CSS.Cssom;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The CSSOM style-sheet / CSS-rule <b>object model</b> feature binding — the sibling of
/// <see cref="StyleDeclarationBinding"/>. It builds the JS <c>CSSRuleList</c>, the per-rule <c>CSSRule</c> objects (every at-rule
/// kind plus style/keyframe rules) and their <c>cssText</c>/<c>insertRule</c>/<c>deleteRule</c>
/// callbacks from the neutral <see cref="Broiler.CSS.CssRule"/> model and the
/// <see cref="Broiler.CSS.Cssom.CssomRuleMetadata"/> projection.
/// <para>
/// Like <see cref="StyleDeclarationBinding"/> it is an <b>internal static class with no host
/// contract</b>: pure CSSOM-IDL logic over the shared rule model, the canonical
/// <c>CssSerializer</c>/<c>CssParser</c>, and <see cref="StyleDeclarationBinding.BuildRuleDeclaration(IJsRealm, RuleDeclarationStore, JsValue)"/>
/// for a rule's <c>style</c>; the one bridge helper it needs is the neutral static
/// <c>DomBridge.ParseStyle</c>. The <em>CSSStyleSheet</em> object itself — its per-element identity
/// cache, the live <c>cssRules</c> collection and the insert/delete mutation bookkeeping that marks the
/// shared model mutated — stays bridge-owned in <c>DomBridge.BuildStyleSheetObject</c> (runtime-state
/// coupled), which calls into this module for the rule objects and hands it, as the sheet's
/// <see cref="StyleSheetRuleModel"/>, the same rule list and mutation signal for a style rule's
/// <c>style</c> to write through.
/// </para>
/// This file holds the CSSOM view of the rule model (which parsed rules a page sees, and the
/// <c>type</c> each answers), the rule-list and keyframe-rule builders; the per-rule <c>CSSRule</c>
/// builder is in <c>StyleSheetBinding.Rules.cs</c> and the <c>JsStyleSheets*Core</c> callbacks in
/// <c>StyleSheetBinding.Callbacks.cs</c>.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so the realm is threaded through every
/// builder rather than an engine type being named — with the single exception of
/// <see cref="RetireIndex"/>, which the contract cannot express; see its remarks.
/// </remarks>
internal static partial class StyleSheetBinding
{
    /// <summary>
    /// At-rules Chromium exposes as grouping rules — a <c>cssRules</c> list of their children — for which
    /// the consumed <see cref="CssomRuleType"/> has no member: <c>CSSContainerRule</c>,
    /// <c>CSSScopeRule</c> and <c>CSSStartingStyleRule</c>. The consumed parser already reads their blocks
    /// as rule lists, so only the CSSOM kind is missing, and adding one to the enum needs a
    /// <c>Broiler.CSS</c> publish; until then the bridge recognises them by name. Reading a block is not
    /// applying it: of the three, the consumed style engine applies only <c>@container</c>.
    /// </summary>
    private static readonly HashSet<string> GroupingAtRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "container", "scope", "starting-style",
    };

    /// <summary>
    /// At-rules Chromium ships and exposes in <c>cssRules</c> whose interfaces the consumed
    /// <see cref="CssomRuleType"/> cannot describe either (<c>CSSFontFeatureValuesRule</c>,
    /// <c>CSSFontPaletteValuesRule</c>, <c>CSSFunctionRule</c>, <c>CSSPositionTryRule</c>,
    /// <c>CSSViewTransitionRule</c>). The bridge gives them their <c>type</c> and <c>cssText</c>, and of
    /// their own members only <c>CSSPositionTryRule.style</c>, which needs nothing but the descriptor
    /// block — a gap, but a smaller one than hiding a rule Chromium shows. A newly
    /// shipped at-rule stays hidden until it is added here, which is also what Chromium did before
    /// shipping it.
    /// </summary>
    private static readonly HashSet<string> OpaqueAtRules = new(StringComparer.OrdinalIgnoreCase)
    {
        "font-feature-values", "font-palette-values", "function", "position-try", "view-transition",
    };

    /// <summary>
    /// <see cref="CssomRuleMetadata.GetRuleType"/>, with <c>@-webkit-keyframes</c> answered as
    /// <see cref="CssomRuleType.Keyframes"/>: Chromium parses it as an alias of <c>@keyframes</c>, and the
    /// consumed metadata matches names exactly.
    /// </summary>
    private static CssomRuleType CssomKindOf(CssRule rule)
    {
        var kind = CssomRuleMetadata.GetRuleType(rule);
        return kind == CssomRuleType.Unknown && rule is CssAtRule atRule &&
               atRule.Name.Equals("-webkit-keyframes", StringComparison.OrdinalIgnoreCase)
            ? CssomRuleType.Keyframes
            : kind;
    }

    /// <summary>
    /// Whether <paramref name="rule"/> is in the CSSOM: every kind the metadata names except
    /// <c>@charset</c>, plus the grouping and opaque at-rules above, each in the form its grammar takes —
    /// <c>@import</c> and <c>@namespace</c> without a block, <c>@layer</c> with or without one, and every
    /// other at-rule with one. Every other at-rule is not.
    /// </summary>
    /// <remarks>
    /// CSSOM "parse a CSS rule" yields no rule for an at-rule the UA does not support, or for one whose
    /// grammar does not match (<c>@media screen;</c>), so Chromium drops one from <c>cssRules</c> and
    /// rejects it from <c>insertRule</c>; and CSS Syntax 3 §8.2 says <c>@charset</c> is not a rule at all
    /// (Chromium has no <c>CSSCharsetRule</c>). The consumed parser keeps all of them, recording whether a
    /// block was present in <see cref="CssAtRule.HasBlock"/>, which is what the form test reads.
    /// <para>
    /// The rules this answers <see langword="false"/> for still stay in the shared model: the renderer and the
    /// <c>getComputedStyle</c> engine read that list (<c>DomBridge.GetStyleElementCssText</c>), so hiding
    /// them there would change what a page renders rather than what it reads. Every sheet-level index is
    /// therefore an index into <see cref="CssomRules"/>, and <see cref="ModelIndexOf"/> turns it back into
    /// a model position.
    /// </para>
    /// </remarks>
    private static bool IsCssomVisible(CssRule rule)
    {
        if (rule is not CssAtRule atRule)
            return rule is CssStyleRule;

        return CssomKindOf(rule) switch
        {
            CssomRuleType.Charset => false,
            CssomRuleType.Import or CssomRuleType.Namespace => !atRule.HasBlock,
            CssomRuleType.Layer => true,
            CssomRuleType.Unknown => atRule.HasBlock &&
                                     (GroupingAtRules.Contains(atRule.Name) || OpaqueAtRules.Contains(atRule.Name)),
            _ => atRule.HasBlock,
        };
    }

    /// <summary>The rules of <paramref name="rules"/> a page sees, in order — see <see cref="IsCssomVisible"/>.</summary>
    internal static List<CssRule> CssomRules(IReadOnlyList<CssRule> rules) => [.. rules.Where(IsCssomVisible)];

    /// <summary>The length of <see cref="CssomRules"/>, counted without building the list.</summary>
    private static int CssomRuleCount(IReadOnlyList<CssRule> rules)
    {
        var count = 0;
        for (var i = 0; i < rules.Count; i++)
        {
            if (IsCssomVisible(rules[i]))
                count++;
        }

        return count;
    }

    /// <summary>
    /// The model position of the visible rule at <paramref name="cssomIndex"/>, or the model's length when
    /// the index is one past the last visible rule. An insert there lands just before that visible rule, so
    /// a hidden rule ahead of it — a leading <c>@charset</c> — keeps its place.
    /// </summary>
    private static int ModelIndexOf(IReadOnlyList<CssRule> rules, int cssomIndex)
    {
        var seen = 0;
        for (var i = 0; i < rules.Count; i++)
        {
            if (!IsCssomVisible(rules[i]))
                continue;
            if (seen == cssomIndex)
                return i;
            seen++;
        }

        return rules.Count;
    }

    /// <summary>
    /// The <c>CSSRule.type</c> a page reads for <paramref name="rule"/>. The consumed
    /// <see cref="CssomRuleType"/> numbers five kinds differently from the IDL constants, and its
    /// <see cref="CssomRuleMetadata.GetCssomTypeNumber"/> is the enum value, so the bridge maps: CSSOM
    /// defines <c>NAMESPACE_RULE</c> 10, css-counter-styles-3 <c>COUNTER_STYLE_RULE</c> 11,
    /// css-conditional-3 <c>SUPPORTS_RULE</c> 12 and css-fonts-4 <c>FONT_FEATURE_VALUES_RULE</c> 14, and
    /// the CSSOM <c>type</c> getter answers 0 for every rule with no constant — <c>@layer</c>,
    /// <c>@property</c> and the grouping and opaque at-rules, as in Chromium.
    /// </summary>
    private static int CssomTypeNumber(CssRule rule, CssomRuleType kind) => kind switch
    {
        CssomRuleType.Namespace => 10,
        CssomRuleType.CounterStyle => 11,
        CssomRuleType.Supports => 12,
        CssomRuleType.Layer or CssomRuleType.Property => 0,
        CssomRuleType.Unknown => rule is CssAtRule atRule &&
                                 atRule.Name.Equals("font-feature-values", StringComparison.OrdinalIgnoreCase) ? 14 : 0,
        _ => (int)kind,
    };

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

    /// <summary>
    /// Builds a rule's nested <c>CSSRuleList</c> over <paramref name="rules"/>. <paramref name="ruleFactory"/>
    /// builds the object for a rule a page inserts, from the rule <c>ParseSingleRule</c> parsed its text
    /// into, and answers <see cref="JsValue.Missing"/> for a rule that does not belong in this list; a list
    /// with no factory (an <c>@layer</c> statement's) accepts no inserts.
    /// </summary>
    private static JsValue BuildCssRuleListObject(IJsRealm realm, List<JsValue> rules, Func<CssRule, JsValue>? ruleFactory = null)
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

        realm.DefineMethod(cssRuleList, "item", 1, (in call) => JsStyleSheetsItem008Core(rules, in call));

        realm.DefineMethod(cssRuleList, "insertRule", 2,
            (in call) => JsStyleSheetsInsertRule009Core(SyncIndices, ruleFactory, rules, in call));

        realm.DefineMethod(cssRuleList, "deleteRule", 1,
            (in call) => JsStyleSheetsDeleteRule010Core(SyncIndices, rules, in call));

        return cssRuleList;
    }

    /// <summary>
    /// Builds the nested rule objects for a grouping rule from its parsed children, leaving out the ones
    /// the CSSOM does not show (<see cref="IsCssomVisible"/>) exactly as a sheet's own list does.
    /// </summary>
    private static List<JsValue> BuildNestedRuleObjects(IJsRealm realm, IReadOnlyList<CssRule> nested,
        JsValue parentStyleSheet,
        JsValue parentRule,
        StyleSheetRuleModel? model) =>
        [.. nested.Where(IsCssomVisible).Select(rule => BuildCssRuleObject(realm, rule, parentStyleSheet, parentRule, model))];

    /// <summary>
    /// Keyframe-rule variant of <see cref="BuildNestedRuleObjects"/>: a keyframe is a style rule whose
    /// selector is the key list, and anything else in a <c>@keyframes</c> block (an <c>@media</c>, say) is
    /// not a keyframe, so Chromium drops it and so does this.
    /// </summary>
    private static List<JsValue> BuildNestedKeyframeObjects(
        IJsRealm realm,
        IReadOnlyList<CssRule> nested,
        JsValue parentStyleSheet,
        JsValue parentRule) =>
        [.. nested.OfType<CssStyleRule>().Select(keyframe => BuildCssKeyframeRuleObject(realm, keyframe, parentStyleSheet, parentRule))];

    private static JsValue BuildCssKeyframeRuleObject(IJsRealm realm, CssStyleRule keyframe, JsValue parentStyleSheet, JsValue parentRule)
    {
        var ruleObj = realm.NewObject();
        realm.DefineAccessor(ruleObj, "parentStyleSheet", (in _) => parentStyleSheet, null);
        realm.DefineAccessor(ruleObj, "parentRule", (in _) => parentRule, null);

        realm.DefineValue(ruleObj, "type", JsValue.Number(8));

        // The key text is the parsed selector list ("0%, 50%"), read from the model with the declarations.
        var keyText = CssomRuleMetadata.GetSelectorText(keyframe);
        realm.DefineValue(ruleObj, "keyText", JsValue.String(keyText));
        realm.DefineAccessor(ruleObj, "cssText",
            (in _) => JsStyleSheetsGetCssText013Core(realm, keyText, ruleObj), null);

        var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(
            realm, DomBridgeUtils.ParseStyle(CssSerializer.Serialize(keyframe.Declarations)), ruleObj);
        realm.DefineValue(ruleObj, "style", styleObj);

        return ruleObj;
    }
}
