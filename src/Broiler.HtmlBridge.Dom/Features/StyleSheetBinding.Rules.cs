using Broiler.CSS;
using Broiler.CSS.Cssom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The per-rule <c>CSSRule</c> object builder half of <see cref="StyleSheetBinding"/> (Phase 3,
/// P3.15): maps a neutral <see cref="Broiler.CSS.CssRule"/> (or a fallback rule string) onto the JS
/// <c>CSSRule</c> object for every rule kind — style, <c>@media</c>/<c>@supports</c>/<c>@layer</c>
/// condition groups, <c>@keyframes</c>, <c>@font-face</c>, <c>@page</c>, <c>@property</c>,
/// <c>@counter-style</c>, <c>@import</c>, <c>@namespace</c> — reading selector/prelude metadata from
/// <see cref="Broiler.CSS.Cssom.CssomRuleMetadata"/> and the declaration block through
/// <see cref="StyleDeclarationBinding.BuildRuleDeclaration"/>.
/// </summary>
internal static partial class StyleSheetBinding
{
    /// <summary>
    /// Builds a CSSRule object from a CSS rule string.
    /// Sets <c>type</c> (1 = CSSStyleRule, 2 = CSSCharsetRule, 3 = CSSImportRule,
    /// 4 = CSSMediaRule, 5 = CSSFontFaceRule, 6 = CSSPageRule, 7 = CSSKeyframesRule,
    /// 9 = CSSNamespaceRule, 10 = CSSCounterStyleRule, 11 = CSSSupportsRule,
    /// 12 = CSSLayerRule, 25 = CSSPropertyRule),
    /// <c>cssText</c>, <c>selectorText</c>, <c>href</c>, <c>media</c>,
    /// <c>conditionText</c>, <c>name</c>, <c>system</c>, <c>symbols</c>,
    /// <c>additiveSymbols</c>, <c>negative</c>, <c>prefix</c>, <c>suffix</c>,
    /// <c>range</c>, <c>pad</c>, <c>fallback</c>, <c>speakAs</c>, <c>syntax</c>,
    /// <c>inherits</c>, <c>initialValue</c>, <c>namespaceURI</c>, <c>cssRules</c>,
    /// and <c>style</c> properties as appropriate.
    /// </summary>
    /// <summary>
    /// Builds a CSSRule object from a shared <see cref="CssRule"/>
    /// model object. Rule kind and metadata (selector text, prelude-derived
    /// media/condition/name/href/prefix values, keyframe keys, and descriptors)
    /// are read from the neutral <see cref="CSS.Cssom.CssomRuleMetadata"/>
    /// projection and the declaration model rather than by serializing the rule and
    /// re-parsing the text. Declaration blocks still feed the JavaScript
    /// <c>CSSStyleDeclaration</c> wrapper through <c>DomBridge.ParseStyle</c> on the
    /// serialized block, which is unchanged. Unrecognized at-rules (for example
    /// <c>@container</c> or a vendor-prefixed <c>@-webkit-keyframes</c>) fall back to
    /// the legacy string builder, preserving their current behavior.
    /// </summary>
    /// <remarks>
    /// A missing <paramref name="parentRule"/> is <see cref="JsValue.Missing"/> rather than a CLR
    /// <see langword="null"/>, and the <c>parentRule</c> getter still answers <c>null</c> for it — the
    /// distinction the contract draws in the direction it is observed.
    /// </remarks>
    internal static JsValue BuildCssRuleObject(IJsRealm realm, CssRule rule, JsValue parentStyleSheet, JsValue parentRule = default)
    {
        var kind = CssomRuleMetadata.GetRuleType(rule);
        if (kind == CssomRuleType.Unknown)
            return BuildCssRuleObject(realm, CssSerializer.Serialize(rule), parentStyleSheet, parentRule);

        var ruleObj = realm.NewObject();
        realm.DefineAccessor(ruleObj, "parentStyleSheet", (in _) => parentStyleSheet, null);
        realm.DefineAccessor(ruleObj, "parentRule",
            (in _) => parentRule.IsMissing ? JsValue.Null : parentRule, null);

        realm.DefineValue(ruleObj, "type", JsValue.Number((int)kind));

        // Builds the JS CSSStyleDeclaration for a declaration-bodied rule from the
        // model's declaration block — identical to the legacy substring path because
        // ParseStyle sees the same declarations, just serialized from the block.
        JsValue StyleFromBlock(CssDeclarationBlock? block)
        {
            var map = block is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : DomBridge.ParseStyle(CssSerializer.Serialize(block));
            return StyleDeclarationBinding.BuildRuleDeclaration(realm, map, ruleObj);
        }

        switch (kind)
        {
            case CssomRuleType.Charset:
                {
                    var encoding = CssomRuleMetadata.GetCharsetEncoding((CssAtRule)rule);
                    realm.DefineValue(ruleObj, "encoding", JsValue.String(encoding));
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsValue.String($"@charset \"{encoding}\";"), null);
                    break;
                }

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
                    var nestedRuleObjects = BuildNestedRuleObjects(realm, string.Empty, atRule.Rules, parentStyleSheet, ruleObj);
                    var nestedCssRules = BuildCssRuleListObject(
                        realm,
                        nestedRuleObjects,
                        text => BuildCssRuleObject(realm, text, parentStyleSheet, ruleObj));

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
                    var nestedRuleObjects = BuildNestedKeyframeObjects(realm, string.Empty, atRule.Rules, parentStyleSheet, ruleObj);
                    var nestedCssRules = BuildCssRuleListObject(realm, nestedRuleObjects,
                        text => BuildCssKeyframeRuleObject(realm, text, parentStyleSheet, ruleObj));

                    realm.DefineValue(ruleObj, "name", JsValue.String(name));
                    realm.DefineValue(ruleObj, "cssRules", nestedCssRules);
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText020Core(realm, name, nestedRuleObjects), null);
                    break;
                }

            case CssomRuleType.Property:
                {
                    var descriptors = DomBridge.ParseStyle(CssSerializer.Serialize(((CssAtRule)rule).Declarations ?? new CssDeclarationBlock([])));
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
                    var descriptors = DomBridge.ParseStyle(CssSerializer.Serialize(atRule.Declarations ?? new CssDeclarationBlock([])));

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
                    var nestedRuleObjects = BuildNestedRuleObjects(realm, string.Empty, atRule.Rules, parentStyleSheet, ruleObj);
                    var nestedCssRules = BuildCssRuleListObject(
                        realm,
                        nestedRuleObjects,
                        text => BuildCssRuleObject(realm, text, parentStyleSheet, ruleObj));

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
                        var nestedRuleObjects = BuildNestedRuleObjects(realm, string.Empty, atRule.Rules, parentStyleSheet, ruleObj);
                        var nestedCssRules = BuildCssRuleListObject(realm, nestedRuleObjects,
                            text => BuildCssRuleObject(realm, text, parentStyleSheet, ruleObj));

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

            default:
                {
                    // CSSStyleRule — type 1
                    var styleRule = (CssStyleRule)rule;
                    var selectorText = CssomRuleMetadata.GetSelectorText(styleRule);
                    realm.DefineValue(ruleObj, "selectorText", JsValue.String(selectorText));
                    realm.DefineAccessor(ruleObj, "cssText",
                        (in _) => JsStyleSheetsGetCssText028Core(realm, ruleObj, selectorText), null);
                    var styleObj = StyleFromBlock(styleRule.Declarations);
                    realm.DefineValue(ruleObj, "style", styleObj);
                    break;
                }
        }

        return ruleObj;
    }

    private static JsValue BuildCssRuleObject(IJsRealm realm, string ruleText, JsValue parentStyleSheet, JsValue parentRule = default, IReadOnlyList<CssRule>? nestedModelRules = null)
    {
        var ruleObj = realm.NewObject();
        realm.DefineAccessor(ruleObj, "parentStyleSheet", (in _) => parentStyleSheet, null);
        realm.DefineAccessor(ruleObj, "parentRule",
            (in _) => parentRule.IsMissing ? JsValue.Null : parentRule, null);

        var trimmedRuleText = ruleText.Trim();

        if (trimmedRuleText.StartsWith("@charset", StringComparison.OrdinalIgnoreCase))
        {
            // CSSCharsetRule — type 2
            realm.DefineValue(ruleObj, "type", JsValue.Number(2));

            var charsetBody = trimmedRuleText[8..].Trim().TrimEnd(';').Trim();
            var encoding = charsetBody.Trim('"', '\'');

            realm.DefineValue(ruleObj, "encoding", JsValue.String(encoding));
            realm.DefineAccessor(ruleObj, "cssText",
                (in _) => JsValue.String($"@charset \"{encoding}\";"), null);
        }
        else if (trimmedRuleText.StartsWith("@import", StringComparison.OrdinalIgnoreCase))
        {
            realm.DefineValue(ruleObj, "type", JsValue.Number(3));

            var importBody = trimmedRuleText[7..].Trim().TrimEnd(';').Trim();
            var href = string.Empty;
            var mediaText = string.Empty;

            if (importBody.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            {
                var openParen = importBody.IndexOf('(');
                var closeParen = importBody.IndexOf(')', openParen + 1);
                if (openParen >= 0 && closeParen > openParen)
                {
                    href = importBody.Substring(openParen + 1, closeParen - openParen - 1).Trim().Trim('"', '\'');
                    mediaText = importBody[(closeParen + 1)..].Trim();
                }
            }
            else if (importBody.StartsWith('"') || importBody.StartsWith('\''))
            {
                var quote = importBody[0];
                var closingQuote = importBody.IndexOf(quote, 1);
                if (closingQuote > 0)
                {
                    href = importBody[1..closingQuote];
                    mediaText = importBody[(closingQuote + 1)..].Trim();
                }
            }

            realm.DefineValue(ruleObj, "href", JsValue.String(href));
            realm.DefineValue(ruleObj, "media", JsValue.String(mediaText));
            realm.DefineAccessor(ruleObj, "cssText",
                (in _) => JsStyleSheetsGetCssText017Core(href, mediaText), null);
        }
        else if (trimmedRuleText.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
        {
            realm.DefineValue(ruleObj, "type", JsValue.Number(4));

            int braceOpen = ruleText.IndexOf('{');
            int braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var mediaText = ruleText[6..braceOpen].Trim();
                var nestedCss = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var nestedRuleObjects = BuildNestedRuleObjects(realm, nestedCss, nestedModelRules, parentStyleSheet, ruleObj);
                var nestedCssRules = BuildCssRuleListObject(
                    realm,
                    nestedRuleObjects,
                    rule => BuildCssRuleObject(realm, rule, parentStyleSheet, ruleObj));

                realm.DefineValue(ruleObj, "media", JsValue.String(mediaText));
                realm.DefineValue(ruleObj, "cssRules", nestedCssRules);
                realm.DefineAccessor(ruleObj, "cssText",
                    (in _) => JsStyleSheetsGetCssText018Core(realm, mediaText, nestedRuleObjects), null);
            }
        }
        else if (trimmedRuleText.StartsWith("@font-face", StringComparison.OrdinalIgnoreCase))
        {
            // CSSFontFaceRule — type 5
            realm.DefineValue(ruleObj, "type", JsValue.Number(5));

            // Extract declarations from @font-face { ... }
            int braceOpen = ruleText.IndexOf('{');
            int braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var declarations = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var styleMap = DomBridge.ParseStyle(declarations);
                var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(realm, styleMap, ruleObj);
                realm.DefineAccessor(ruleObj, "cssText",
                    (in _) => JsStyleSheetsGetCssText019Core(realm, styleObj), null);
                realm.DefineValue(ruleObj, "style", styleObj);
            }
        }
        else if (trimmedRuleText.StartsWith("@keyframes", StringComparison.OrdinalIgnoreCase))
        {
            // CSSKeyframesRule — type 7
            realm.DefineValue(ruleObj, "type", JsValue.Number(7));

            int braceOpen = ruleText.IndexOf('{');
            int braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var name = ruleText[10..braceOpen].Trim().Trim('"', '\'');
                var nestedCss = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var nestedRuleObjects = BuildNestedKeyframeObjects(realm, nestedCss, nestedModelRules, parentStyleSheet, ruleObj);
                var nestedCssRules = BuildCssRuleListObject(
                    realm,
                    nestedRuleObjects,
                    rule => BuildCssKeyframeRuleObject(realm, rule, parentStyleSheet, ruleObj));

                realm.DefineValue(ruleObj, "name", JsValue.String(name));
                realm.DefineValue(ruleObj, "cssRules", nestedCssRules);
                realm.DefineAccessor(ruleObj, "cssText",
                    (in _) => JsStyleSheetsGetCssText020Core(realm, name, nestedRuleObjects), null);
            }
        }
        else if (trimmedRuleText.StartsWith("@property", StringComparison.OrdinalIgnoreCase))
        {
            // CSSPropertyRule — type 25
            realm.DefineValue(ruleObj, "type", JsValue.Number(25));

            var braceOpen = trimmedRuleText.IndexOf('{');
            var braceClose = trimmedRuleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var propertyName = trimmedRuleText[9..braceOpen].Trim();
                var descriptorsText = trimmedRuleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var descriptors = DomBridge.ParseStyle(descriptorsText);
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
            }
        }
        else if (trimmedRuleText.StartsWith("@counter-style", StringComparison.OrdinalIgnoreCase))
        {
            // CSSCounterStyleRule — type 10
            realm.DefineValue(ruleObj, "type", JsValue.Number(10));

            var braceOpen = trimmedRuleText.IndexOf('{');
            var braceClose = trimmedRuleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var ruleName = trimmedRuleText[14..braceOpen].Trim();
                var descriptorsText = trimmedRuleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var descriptors = DomBridge.ParseStyle(descriptorsText);

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
            }
        }
        else if (trimmedRuleText.StartsWith("@supports", StringComparison.OrdinalIgnoreCase))
        {
            // CSSSupportsRule — type 11
            realm.DefineValue(ruleObj, "type", JsValue.Number(11));

            int braceOpen = ruleText.IndexOf('{');
            int braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var conditionText = ruleText[9..braceOpen].Trim();
                var nestedCss = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var nestedRuleObjects = BuildNestedRuleObjects(realm, nestedCss, nestedModelRules, parentStyleSheet, ruleObj);
                var nestedCssRules = BuildCssRuleListObject(realm, nestedRuleObjects,
                    rule => BuildCssRuleObject(realm, rule, parentStyleSheet, ruleObj));

                realm.DefineValue(ruleObj, "conditionText", JsValue.String(conditionText));
                realm.DefineValue(ruleObj, "cssRules", nestedCssRules);
                realm.DefineAccessor(ruleObj, "cssText",
                    (in _) => JsStyleSheetsGetCssText023Core(realm, conditionText, nestedRuleObjects), null);
            }
        }
        else if (trimmedRuleText.StartsWith("@layer", StringComparison.OrdinalIgnoreCase))
        {
            // CSSLayerRule — type 12
            realm.DefineValue(ruleObj, "type", JsValue.Number(12));

            var layerBody = ruleText[6..].Trim();
            var braceOpen = ruleText.IndexOf('{');
            var braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var nameText = ruleText[6..braceOpen].Trim();
                var nestedCss = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var nestedRuleObjects = BuildNestedRuleObjects(realm, nestedCss, nestedModelRules, parentStyleSheet, ruleObj);
                var nestedCssRules = BuildCssRuleListObject(realm, nestedRuleObjects,
                    rule => BuildCssRuleObject(realm, rule, parentStyleSheet, ruleObj));

                realm.DefineValue(ruleObj, "name",
                    string.IsNullOrEmpty(nameText) ? JsValue.Null : JsValue.String(nameText));
                realm.DefineValue(ruleObj, "cssRules", nestedCssRules);
                realm.DefineAccessor(ruleObj, "cssText",
                    (in _) => JsStyleSheetsGetCssText024Core(realm, nameText, nestedRuleObjects), null);
            }
            else
            {
                var nameText = layerBody.TrimEnd(';').Trim();
                realm.DefineValue(ruleObj, "name",
                    string.IsNullOrEmpty(nameText) ? JsValue.Null : JsValue.String(nameText));
                realm.DefineValue(ruleObj, "cssRules", BuildCssRuleListObject(realm, []));
                realm.DefineAccessor(ruleObj, "cssText",
                    (in _) => JsStyleSheetsGetCssText025Core(nameText), null);
            }
        }
        else if (trimmedRuleText.StartsWith("@namespace", StringComparison.OrdinalIgnoreCase))
        {
            // CSSNamespaceRule — type 9
            realm.DefineValue(ruleObj, "type", JsValue.Number(9));

            var namespaceBody = trimmedRuleText[10..].Trim().TrimEnd(';').Trim();
            string? prefix = null;
            var namespaceUri = string.Empty;

            var parts = namespaceBody.Split([' ', '\t', '\r', '\n'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                prefix = parts[0];
                namespaceUri = CssomRuleMetadata.ExtractNamespaceUri(parts[1]);
            }
            else if (parts.Length == 1)
            {
                namespaceUri = CssomRuleMetadata.ExtractNamespaceUri(parts[0]);
            }

            realm.DefineValue(ruleObj, "namespaceURI", JsValue.String(namespaceUri));
            realm.DefineValue(ruleObj, "prefix",
                string.IsNullOrEmpty(prefix) ? JsValue.Undefined : JsValue.String(prefix));
            realm.DefineAccessor(ruleObj, "cssText",
                (in _) => JsStyleSheetsGetCssText026Core(namespaceUri, prefix), null);
        }
        else if (trimmedRuleText.StartsWith("@page", StringComparison.OrdinalIgnoreCase))
        {
            // CSSPageRule — type 6
            realm.DefineValue(ruleObj, "type", JsValue.Number(6));

            var braceOpen = ruleText.IndexOf('{');
            var braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var selectorText = ruleText[5..braceOpen].Trim();
                var declarations = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var styleMap = DomBridge.ParseStyle(declarations);
                var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(realm, styleMap, ruleObj);

                realm.DefineValue(ruleObj, "selectorText", JsValue.String(selectorText));
                realm.DefineValue(ruleObj, "style", styleObj);
                realm.DefineAccessor(ruleObj, "cssText",
                    (in _) => JsStyleSheetsGetCssText027Core(realm, selectorText, styleObj), null);
            }
        }
        else
        {
            // CSSStyleRule — type 1
            realm.DefineValue(ruleObj, "type", JsValue.Number(1));

            // Extract selector text
            int braceOpen = ruleText.IndexOf('{');
            if (braceOpen >= 0)
            {
                var selectorText = ruleText[..braceOpen].Trim();
                realm.DefineValue(ruleObj, "selectorText", JsValue.String(selectorText));
                realm.DefineAccessor(ruleObj, "cssText",
                    (in _) => JsStyleSheetsGetCssText028Core(realm, ruleObj, selectorText), null);

                int braceClose = ruleText.LastIndexOf('}');
                if (braceClose > braceOpen)
                {
                    var declarations = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                    var styleMap = DomBridge.ParseStyle(declarations);
                    var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(realm, styleMap, ruleObj);
                    realm.DefineValue(ruleObj, "style", styleObj);
                }
            }
        }

        return ruleObj;
    }
}
