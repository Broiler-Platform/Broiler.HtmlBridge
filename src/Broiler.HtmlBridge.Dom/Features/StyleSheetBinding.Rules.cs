using Broiler.CSS;
using Broiler.CSS.Cssom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The per-rule <c>CSSRule</c> object builder half of <see cref="StyleSheetBinding"/> (Phase 3,
/// P3.15): maps a parsed <see cref="Broiler.CSS.CssRule"/> onto the JS <c>CSSRule</c> object for every
/// rule kind the CSSOM shows — style, <c>@media</c>/<c>@supports</c>/<c>@layer</c> condition groups,
/// <c>@keyframes</c> (and its <c>@-webkit-</c> alias), <c>@font-face</c>, <c>@page</c>, <c>@property</c>,
/// <c>@counter-style</c>, <c>@import</c>, <c>@namespace</c>, the <c>@container</c>/<c>@scope</c>/
/// <c>@starting-style</c> grouping rules and the at-rules exposed with <c>type</c> and <c>cssText</c>
/// (and, for <c>@position-try</c>, <c>style</c>) — reading selector/prelude metadata from
/// <see cref="Broiler.CSS.Cssom.CssomRuleMetadata"/> and the declaration block through
/// <see cref="StyleDeclarationBinding.BuildRuleDeclaration(IJsRealm, RuleDeclarationStore, JsValue)"/>.
/// </summary>
internal static partial class StyleSheetBinding
{
    /// <summary>
    /// Builds a CSSRule object from a shared <see cref="CssRule"/> model object. Rule kind and metadata
    /// (selector text, prelude-derived media/condition/name/href/prefix values, keyframe keys, and
    /// descriptors) are read from the neutral <see cref="CSS.Cssom.CssomRuleMetadata"/> projection and the
    /// declaration model; declaration blocks feed the JavaScript <c>CSSStyleDeclaration</c> wrapper through
    /// <c>DomBridge.ParseStyle</c> on the serialized block. Sets <c>type</c> (1 = CSSStyleRule,
    /// 3 = CSSImportRule, 4 = CSSMediaRule, 5 = CSSFontFaceRule, 6 = CSSPageRule, 7 = CSSKeyframesRule,
    /// 10 = CSSNamespaceRule, 11 = CSSCounterStyleRule, 12 = CSSSupportsRule,
    /// 14 = CSSFontFeatureValuesRule, 0 for every other kind — see <see cref="CssomTypeNumber"/>),
    /// <c>cssText</c>, <c>selectorText</c>, <c>href</c>, <c>media</c>, <c>conditionText</c>, <c>name</c>,
    /// <c>system</c>, <c>symbols</c>, <c>additiveSymbols</c>, <c>negative</c>, <c>prefix</c>, <c>suffix</c>,
    /// <c>range</c>, <c>pad</c>, <c>fallback</c>, <c>speakAs</c>, <c>syntax</c>, <c>inherits</c>,
    /// <c>initialValue</c>, <c>namespaceURI</c>, <c>cssRules</c>, and <c>style</c> properties as
    /// appropriate.
    /// </summary>
    /// <remarks>
    /// Every caller passes only rules <see cref="IsCssomVisible"/> accepts — the sheet's live list reads
    /// <see cref="CssomRules"/>, the nested builders filter the same way, and an insert rejects the rest
    /// first — so a <c>@charset</c> or an unsupported at-rule never reaches this.
    /// <para>
    /// A missing <paramref name="parentRule"/> is <see cref="JsValue.Missing"/> rather than a CLR
    /// <see langword="null"/>, and the <c>parentRule</c> getter still answers <c>null</c> for it — the
    /// distinction the contract draws in the direction it is observed.
    /// </para>
    /// <para>
    /// With a <paramref name="model"/>, a style rule's <c>style</c> — at the top level or inside grouping
    /// rules, which pass the model down — writes through to the sheet
    /// (<see cref="ModelRuleDeclarationStore"/>). Without one, and for every other declaration block, the
    /// declaration is a map that lives only on the object (<see cref="MapRuleDeclarationStore"/> says why).
    /// </para>
    /// </remarks>
    internal static JsValue BuildCssRuleObject(IJsRealm realm, CssRule rule, JsValue parentStyleSheet,
        JsValue parentRule = default, StyleSheetRuleModel? model = null)
    {
        var kind = CssomKindOf(rule);

        var ruleObj = realm.NewObject();
        realm.DefineAccessor(ruleObj, "parentStyleSheet", (in _) => parentStyleSheet, null);
        realm.DefineAccessor(ruleObj, "parentRule",
            (in _) => parentRule.IsMissing ? JsValue.Null : parentRule, null);

        realm.DefineValue(ruleObj, "type", JsValue.Number(CssomTypeNumber(rule, kind)));

        // Builds the JS CSSStyleDeclaration for a declaration-bodied rule from the model's declaration
        // block, serialized so ParseStyle applies the same per-property validation an inline style gets.
        JsValue StyleFromBlock(CssDeclarationBlock? block)
        {
            var map = block is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : DomBridgeUtils.ParseStyle(CssSerializer.Serialize(block));
            return StyleDeclarationBinding.BuildRuleDeclaration(realm, map, ruleObj);
        }

        // The factory a grouping rule's cssRules list builds an inserted rule with. The page's text has
        // been parsed by then; a rule the CSSOM does not show answers Missing, which the list turns into
        // the SyntaxError a sheet-level insert of the same text throws. The model goes along so the inserted
        // rule's style is built the same way, but such a rule is never in the sheet's rule list, so its
        // writes stay on the object (see the StyleSheetRuleModel remarks).
        JsValue NestedRule(CssRule child) =>
            IsCssomVisible(child) ? BuildCssRuleObject(realm, child, parentStyleSheet, ruleObj, model) : JsValue.Missing;

        switch (kind)
        {
            case CssomRuleType.Import:
                {
                    var import = CssomRuleMetadata.GetImport((CssAtRule)rule);
                    realm.DefineValue(ruleObj, "href", JsValue.String(import.Href));
                    realm.DefineValue(ruleObj, "media", JsValue.String(import.Media));
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText017Core(import.Href, import.Media), null);
                    break;
                }

            case CssomRuleType.Media:
                {
                    var atRule = (CssAtRule)rule;
                    var mediaText = atRule.Prelude;
                    var nestedRuleObjects = BuildNestedRuleObjects(realm, atRule.Rules, parentStyleSheet, ruleObj, model);
                    var nestedCssRules = BuildCssRuleListObject(realm, nestedRuleObjects, NestedRule);

                    realm.DefineValue(ruleObj, "media", JsValue.String(mediaText));
                    realm.DefineValue(ruleObj, "cssRules", nestedCssRules);
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText018Core(realm, mediaText, nestedRuleObjects), null);
                    break;
                }

            case CssomRuleType.FontFace:
                {
                    var styleObj = StyleFromBlock(((CssAtRule)rule).Declarations);
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText019Core(realm, styleObj), null);
                    realm.DefineValue(ruleObj, "style", styleObj);
                    break;
                }

            case CssomRuleType.Keyframes:
                {
                    var atRule = (CssAtRule)rule;
                    var name = CssomRuleMetadata.GetKeyframesName(atRule);
                    var nestedRuleObjects = BuildNestedKeyframeObjects(realm, atRule.Rules, parentStyleSheet, ruleObj);
                    var nestedCssRules = BuildCssRuleListObject(realm, nestedRuleObjects,
                        child => child is CssStyleRule keyframe
                            ? BuildCssKeyframeRuleObject(realm, keyframe, parentStyleSheet, ruleObj)
                            : JsValue.Missing);

                    realm.DefineValue(ruleObj, "name", JsValue.String(name));
                    realm.DefineValue(ruleObj, "cssRules", nestedCssRules);
                    // The at-rule's own name, so @-webkit-keyframes keeps its prefix as Chromium's does.
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText020Core(realm, atRule.Name, name, nestedRuleObjects), null);
                    break;
                }

            case CssomRuleType.Property:
                {
                    var descriptors = DomBridgeUtils.ParseStyle(CssSerializer.Serialize(((CssAtRule)rule).Declarations ?? new CssDeclarationBlock([])));
                    var propertyName = ((CssAtRule)rule).Prelude;
                    var syntax = descriptors.TryGetValue("syntax", out var syntaxValue)
                        ? CssomRuleMetadata.UnquoteDescriptor(syntaxValue)
                        : "*";
                    var inherits = !descriptors.TryGetValue("inherits", out var inheritsValue)
                        || !string.Equals(inheritsValue, "false", StringComparison.OrdinalIgnoreCase);
                    var initialValue = descriptors.GetValueOrDefault("initial-value");

                    realm.DefineValue(ruleObj, "name", JsValue.String(propertyName));
                    realm.DefineValue(ruleObj, "syntax", JsValue.String(syntax));
                    realm.DefineValue(ruleObj, "inherits", JsValue.Boolean(inherits));
                    realm.DefineValue(ruleObj, "initialValue",
                        string.IsNullOrEmpty(initialValue) ? JsValue.Null : JsValue.String(initialValue));
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText021Core(inherits, initialValue, propertyName, syntax), null);
                    break;
                }

            case CssomRuleType.CounterStyle:
                {
                    var atRule = (CssAtRule)rule;
                    var ruleName = atRule.Prelude;
                    var descriptors = DomBridgeUtils.ParseStyle(CssSerializer.Serialize(atRule.Declarations ?? new CssDeclarationBlock([])));

                    realm.DefineValue(ruleObj, "name", JsValue.String(ruleName));

                    var descriptorMap = new (string CssName, string JsName)[]
                    {
                        ("system", "system"),
                        ("symbols", "symbols"),
                        ("additive-symbols", "additiveSymbols"),
                        ("negative", "negative"),
                        ("prefix", "prefix"),
                        ("suffix", "suffix"),
                        ("range", "range"),
                        ("pad", "pad"),
                        ("fallback", "fallback"),
                        ("speak-as", "speakAs")
                    };

                    foreach (var (cssName, jsName) in descriptorMap)
                    {
                        realm.DefineValue(ruleObj, jsName,
                            descriptors.TryGetValue(cssName, out var value) ? JsValue.String(value) : JsValue.Undefined);
                    }

                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText022Core(realm, descriptorMap, ruleName, ruleObj), null);
                    break;
                }

            case CssomRuleType.Supports:
                {
                    var atRule = (CssAtRule)rule;
                    var conditionText = atRule.Prelude;
                    var nestedRuleObjects = BuildNestedRuleObjects(realm, atRule.Rules, parentStyleSheet, ruleObj, model);
                    var nestedCssRules = BuildCssRuleListObject(realm, nestedRuleObjects, NestedRule);

                    realm.DefineValue(ruleObj, "conditionText", JsValue.String(conditionText));
                    realm.DefineValue(ruleObj, "cssRules", nestedCssRules);
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText023Core(realm, conditionText, nestedRuleObjects), null);
                    break;
                }

            case CssomRuleType.Layer:
                {
                    var atRule = (CssAtRule)rule;
                    var nameText = atRule.Prelude;
                    if (atRule.HasBlock)
                    {
                        var nestedRuleObjects = BuildNestedRuleObjects(realm, atRule.Rules, parentStyleSheet, ruleObj, model);
                        var nestedCssRules = BuildCssRuleListObject(realm, nestedRuleObjects, NestedRule);

                        realm.DefineValue(ruleObj, "name",
                            string.IsNullOrEmpty(nameText) ? JsValue.Null : JsValue.String(nameText));
                        realm.DefineValue(ruleObj, "cssRules", nestedCssRules);
                        realm.DefineAccessor(ruleObj, "cssText",
                            (in _) => JsStyleSheetsGetCssText024Core(realm, nameText, nestedRuleObjects), null);
                    }
                    else
                    {
                        // Statement form: `@layer a, b;` — no block, empty cssRules.
                        realm.DefineValue(ruleObj, "name",
                            string.IsNullOrEmpty(nameText) ? JsValue.Null : JsValue.String(nameText));
                        realm.DefineValue(ruleObj, "cssRules", BuildCssRuleListObject(realm, []));
                        realm.DefineAccessor(ruleObj, "cssText",
                            (in _) => JsStyleSheetsGetCssText025Core(nameText), null);
                    }
                    break;
                }

            case CssomRuleType.Namespace:
                {
                    var ns = CssomRuleMetadata.GetNamespace((CssAtRule)rule);
                    realm.DefineValue(ruleObj, "namespaceURI", JsValue.String(ns.Uri));
                    realm.DefineValue(ruleObj, "prefix",
                        string.IsNullOrEmpty(ns.Prefix) ? JsValue.Undefined : JsValue.String(ns.Prefix));
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText026Core(ns.Uri, ns.Prefix), null);
                    break;
                }

            case CssomRuleType.Page:
                {
                    var atRule = (CssAtRule)rule;
                    var selectorText = atRule.Prelude;
                    var styleObj = StyleFromBlock(atRule.Declarations);
                    realm.DefineValue(ruleObj, "selectorText", JsValue.String(selectorText));
                    realm.DefineValue(ruleObj, "style", styleObj);
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText027Core(realm, selectorText, styleObj), null);
                    break;
                }

            case CssomRuleType.Style:
                {
                    var styleRule = (CssStyleRule)rule;
                    var selectorText = CssomRuleMetadata.GetSelectorText(styleRule);
                    realm.DefineValue(ruleObj, "selectorText", JsValue.String(selectorText));
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText028Core(realm, ruleObj, selectorText), null);
                    var styleObj = model is null
                        ? StyleFromBlock(styleRule.Declarations)
                        : StyleDeclarationBinding.BuildRuleDeclaration(
                            realm, new ModelRuleDeclarationStore(model, model.CellFor(styleRule)), ruleObj);
                    realm.DefineValue(ruleObj, "style", styleObj);
                    break;
                }

            default:
                {
                    // An at-rule the metadata has no kind for and IsCssomVisible still shows: one of
                    // GroupingAtRules or OpaqueAtRules.
                    var atRule = (CssAtRule)rule;
                    if (GroupingAtRules.Contains(atRule.Name))
                    {
                        var nestedRuleObjects = BuildNestedRuleObjects(realm, atRule.Rules, parentStyleSheet, ruleObj, model);
                        realm.DefineValue(ruleObj, "cssRules", BuildCssRuleListObject(realm, nestedRuleObjects, NestedRule));
                        // CSSContainerRule.conditionText is the whole prelude, container name included; the
                        // scope and starting-style rules are grouping rules without a condition.
                        if (atRule.Name.Equals("container", StringComparison.OrdinalIgnoreCase))
                            realm.DefineValue(ruleObj, "conditionText", JsValue.String(atRule.Prelude));
                        realm.DefineAccessor(ruleObj, "cssText",
                            (in _) => JsStyleSheetsGetCssText029Core(realm, atRule.Name, atRule.Prelude, nestedRuleObjects), null);
                    }
                    else
                    {
                        // Opaque: the body is the block as parsed — declarations when the parser reads it
                        // as a descriptor block, otherwise its text without comments.
                        var body = atRule.Declarations is { } declarations
                            ? string.Join(" ", declarations.Declarations.Select(declaration =>
                                $"{declaration.Name}: {declaration.Value.Text}{(declaration.Important ? " !important" : string.Empty)};"))
                            : CssSyntax.RemoveComments(atRule.BlockText ?? string.Empty).Trim();
                        realm.DefineAccessor(ruleObj, "cssText",
                            (in _) => JsStyleSheetsGetCssText030Core(atRule.Name, atRule.Prelude, body), null);

                        // CSSPositionTryRule does have a member the page can use without a rule kind of
                        // its own: `style`, its descriptor block. The parser keeps that block only as
                        // text, so it is parsed here the way a descriptor block is.
                        if (atRule.Name.Equals("position-try", StringComparison.OrdinalIgnoreCase))
                            realm.DefineValue(ruleObj, "style", StyleFromBlock(
                                new CssParser().ParseDeclarations(CssSyntax.RemoveComments(atRule.BlockText ?? string.Empty))));
                    }
                    break;
                }
        }

        return ruleObj;
    }
}
