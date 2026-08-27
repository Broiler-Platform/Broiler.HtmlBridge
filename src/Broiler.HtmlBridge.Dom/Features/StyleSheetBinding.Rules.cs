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
    /// Builds a CSSRule JSObject from a CSS rule string.
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
    /// Builds a CSSRule JSObject from a shared <see cref="CssRule"/>
    /// model object. Rule kind and metadata (selector text, prelude-derived
    /// media/condition/name/href/prefix values, keyframe keys, and descriptors)
    /// are read from the neutral <see cref="CSS.Cssom.CssomRuleMetadata"/>
    /// projection and the declaration model rather than by serializing the rule and
    /// re-parsing the text. Declaration blocks still feed the JavaScript
    /// <c>CSSStyleDeclaration</c> wrapper through <see cref="ParseStyle"/> on the
    /// serialized block, which is unchanged. Unrecognized at-rules (for example
    /// <c>@container</c> or a vendor-prefixed <c>@-webkit-keyframes</c>) fall back to
    /// the legacy string builder, preserving their current behavior.
    /// </summary>
    internal static JSObject BuildCssRuleObject(CssRule rule, JSObject parentStyleSheet, JSObject? parentRule = null)
    {
        var kind = CssomRuleMetadata.GetRuleType(rule);
        if (kind == CssomRuleType.Unknown)
            return BuildCssRuleObject(CssSerializer.Serialize(rule), parentStyleSheet, parentRule);

        var ruleObj = new JSObject();
        ruleObj.FastAddProperty("parentStyleSheet",
            new DomFunction((in _) => parentStyleSheet, "get parentStyleSheet"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        ruleObj.FastAddProperty("parentRule",
            new DomFunction((in _) => parentRule ?? JSNull.Value, "get parentRule"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        ruleObj.FastAddValue("type", new JSNumber((int)kind), JSPropertyAttributes.EnumerableConfigurableValue);

        // Builds the JS CSSStyleDeclaration for a declaration-bodied rule from the
        // model's declaration block — identical to the legacy substring path because
        // ParseStyle sees the same declarations, just serialized from the block.
        JSObject StyleFromBlock(CssDeclarationBlock? block)
        {
            var map = block is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : DomBridge.ParseStyle(CssSerializer.Serialize(block));
            return StyleDeclarationBinding.BuildRuleDeclaration(map, ruleObj);
        }

        switch (kind)
        {
            case CssomRuleType.Charset:
                {
                    var encoding = CssomRuleMetadata.GetCharsetEncoding((CssAtRule)rule);
                    ruleObj.FastAddValue("encoding", new JSString(encoding), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => new JSString($"@charset \"{encoding}\";"), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    break;
                }

            case CssomRuleType.Import:
                {
                    var import = CssomRuleMetadata.GetImport((CssAtRule)rule);
                    ruleObj.FastAddValue("href", new JSString(import.Href), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddValue("media", new JSString(import.Media), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText017Core(import.Href, import.Media, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    break;
                }

            case CssomRuleType.Media:
                {
                    var atRule = (CssAtRule)rule;
                    var mediaText = atRule.Prelude;
                    var nestedRuleObjects = BuildNestedRuleObjects(string.Empty, atRule.Rules, parentStyleSheet, ruleObj);
                    var nestedCssRules = BuildCssRuleListObject(
                        nestedRuleObjects,
                        text => BuildCssRuleObject(text, parentStyleSheet, ruleObj));

                    ruleObj.FastAddValue("media", new JSString(mediaText), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddValue("cssRules", nestedCssRules, JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText018Core(mediaText, nestedRuleObjects, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    break;
                }

            case CssomRuleType.FontFace:
                {
                    var styleObj = StyleFromBlock(((CssAtRule)rule).Declarations);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText019Core(styleObj, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    ruleObj.FastAddValue("style", styleObj, JSPropertyAttributes.EnumerableConfigurableValue);
                    break;
                }

            case CssomRuleType.Keyframes:
                {
                    var atRule = (CssAtRule)rule;
                    var name = CssomRuleMetadata.GetKeyframesName(atRule);
                    var nestedRuleObjects = BuildNestedKeyframeObjects(string.Empty, atRule.Rules, parentStyleSheet, ruleObj);
                    var nestedCssRules = BuildCssRuleListObject(nestedRuleObjects,
                        text => BuildCssKeyframeRuleObject(text, parentStyleSheet, ruleObj));

                    ruleObj.FastAddValue("name", new JSString(name), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddValue("cssRules", nestedCssRules, JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText020Core(name, nestedRuleObjects, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
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

                    ruleObj.FastAddValue("name", new JSString(propertyName), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddValue("syntax", new JSString(syntax), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddValue("inherits", inherits ? JSBoolean.True : JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddValue("initialValue",
                        string.IsNullOrEmpty(initialValue) ? JSNull.Value : new JSString(initialValue),
                        JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText021Core(inherits, initialValue, propertyName, syntax, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    break;
                }

            case CssomRuleType.CounterStyle:
                {
                    var atRule = (CssAtRule)rule;
                    var ruleName = atRule.Prelude;
                    var descriptors = DomBridge.ParseStyle(CssSerializer.Serialize(atRule.Declarations ?? new CssDeclarationBlock([])));

                    ruleObj.FastAddValue("name", new JSString(ruleName), JSPropertyAttributes.EnumerableConfigurableValue);

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
                        ruleObj.FastAddValue(jsName,
                            descriptors.TryGetValue(cssName, out var value) ? new JSString(value) : JSUndefined.Value,
                            JSPropertyAttributes.EnumerableConfigurableValue);
                    }

                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText022Core(descriptorMap, ruleName, ruleObj, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    break;
                }

            case CssomRuleType.Supports:
                {
                    var atRule = (CssAtRule)rule;
                    var conditionText = atRule.Prelude;
                    var nestedRuleObjects = BuildNestedRuleObjects(string.Empty, atRule.Rules, parentStyleSheet, ruleObj);
                    var nestedCssRules = BuildCssRuleListObject(
                        nestedRuleObjects,
                        text => BuildCssRuleObject(text, parentStyleSheet, ruleObj));

                    ruleObj.FastAddValue("conditionText", new JSString(conditionText), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddValue("cssRules", nestedCssRules, JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText023Core(conditionText, nestedRuleObjects, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    break;
                }

            case CssomRuleType.Layer:
                {
                    var atRule = (CssAtRule)rule;
                    var nameText = atRule.Prelude;
                    if (atRule.HasBlock)
                    {
                        var nestedRuleObjects = BuildNestedRuleObjects(string.Empty, atRule.Rules, parentStyleSheet, ruleObj);
                        var nestedCssRules = BuildCssRuleListObject(nestedRuleObjects,
                            text => BuildCssRuleObject(text, parentStyleSheet, ruleObj));

                        ruleObj.FastAddValue("name",
                            string.IsNullOrEmpty(nameText) ? JSNull.Value : new JSString(nameText),
                            JSPropertyAttributes.EnumerableConfigurableValue);
                        ruleObj.FastAddValue("cssRules", nestedCssRules, JSPropertyAttributes.EnumerableConfigurableValue);
                        ruleObj.FastAddProperty("cssText",
                            new DomFunction((in _) => JsStyleSheetsGetCssText024Core(nameText, nestedRuleObjects, in _), "get cssText"),
                            null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    }
                    else
                    {
                        // Statement form: `@layer a, b;` — no block, empty cssRules.
                        ruleObj.FastAddValue("name",
                            string.IsNullOrEmpty(nameText) ? JSNull.Value : new JSString(nameText),
                            JSPropertyAttributes.EnumerableConfigurableValue);
                        ruleObj.FastAddValue("cssRules", BuildCssRuleListObject([]), JSPropertyAttributes.EnumerableConfigurableValue);
                        ruleObj.FastAddProperty("cssText",
                            new DomFunction((in _) => JsStyleSheetsGetCssText025Core(nameText, in _), "get cssText"),
                            null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    }
                    break;
                }

            case CssomRuleType.Namespace:
                {
                    var ns = CssomRuleMetadata.GetNamespace((CssAtRule)rule);
                    ruleObj.FastAddValue("namespaceURI", new JSString(ns.Uri), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddValue("prefix",
                        string.IsNullOrEmpty(ns.Prefix) ? JSUndefined.Value : new JSString(ns.Prefix),
                        JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText026Core(ns.Uri, ns.Prefix, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    break;
                }

            case CssomRuleType.Page:
                {
                    var atRule = (CssAtRule)rule;
                    var selectorText = atRule.Prelude;
                    var styleObj = StyleFromBlock(atRule.Declarations);
                    ruleObj.FastAddValue("selectorText", new JSString(selectorText), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddValue("style", styleObj, JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText027Core(selectorText, styleObj, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    break;
                }

            default:
                {
                    // CSSStyleRule — type 1
                    var styleRule = (CssStyleRule)rule;
                    var selectorText = CssomRuleMetadata.GetSelectorText(styleRule);
                    ruleObj.FastAddValue("selectorText", new JSString(selectorText), JSPropertyAttributes.EnumerableConfigurableValue);
                    ruleObj.FastAddProperty("cssText",
                        new DomFunction((in _) => JsStyleSheetsGetCssText028Core(ruleObj, selectorText, in _), "get cssText"),
                        null, JSPropertyAttributes.EnumerableConfigurableProperty);
                    var styleObj = StyleFromBlock(styleRule.Declarations);
                    ruleObj.FastAddValue("style", styleObj, JSPropertyAttributes.EnumerableConfigurableValue);
                    break;
                }
        }

        return ruleObj;
    }

    private static JSObject BuildCssRuleObject(string ruleText, JSObject parentStyleSheet, JSObject? parentRule = null, IReadOnlyList<CssRule>? nestedModelRules = null)
    {
        var ruleObj = new JSObject();
        ruleObj.FastAddProperty(
            "parentStyleSheet",
            new DomFunction((in _) => parentStyleSheet, "get parentStyleSheet"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        ruleObj.FastAddProperty("parentRule",
            new DomFunction((in _) => parentRule ?? JSNull.Value, "get parentRule"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        var trimmedRuleText = ruleText.Trim();

        if (trimmedRuleText.StartsWith("@charset", StringComparison.OrdinalIgnoreCase))
        {
            // CSSCharsetRule — type 2
            ruleObj.FastAddValue("type", new JSNumber(2), JSPropertyAttributes.EnumerableConfigurableValue);

            var charsetBody = trimmedRuleText[8..].Trim().TrimEnd(';').Trim();
            var encoding = charsetBody.Trim('"', '\'');

            ruleObj.FastAddValue("encoding", new JSString(encoding), JSPropertyAttributes.EnumerableConfigurableValue);
            ruleObj.FastAddProperty("cssText",
                new DomFunction((in _) => new JSString($"@charset \"{encoding}\";"), "get cssText"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);
        }
        else if (trimmedRuleText.StartsWith("@import", StringComparison.OrdinalIgnoreCase))
        {
            ruleObj.FastAddValue("type", new JSNumber(3), JSPropertyAttributes.EnumerableConfigurableValue);

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

            ruleObj.FastAddValue("href", new JSString(href), JSPropertyAttributes.EnumerableConfigurableValue);
            ruleObj.FastAddValue("media", new JSString(mediaText), JSPropertyAttributes.EnumerableConfigurableValue);
            ruleObj.FastAddProperty("cssText",
                new DomFunction((in _) => JsStyleSheetsGetCssText017Core(href, mediaText, in _), "get cssText"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);
        }
        else if (trimmedRuleText.StartsWith("@media", StringComparison.OrdinalIgnoreCase))
        {
            ruleObj.FastAddValue("type", new JSNumber(4), JSPropertyAttributes.EnumerableConfigurableValue);

            int braceOpen = ruleText.IndexOf('{');
            int braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var mediaText = ruleText[6..braceOpen].Trim();
                var nestedCss = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var nestedRuleObjects = BuildNestedRuleObjects(nestedCss, nestedModelRules, parentStyleSheet, ruleObj);
                var nestedCssRules = BuildCssRuleListObject(
                    nestedRuleObjects,
                    rule => BuildCssRuleObject(rule, parentStyleSheet, ruleObj));

                ruleObj.FastAddValue("media", new JSString(mediaText), JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddValue("cssRules", nestedCssRules, JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText018Core(mediaText, nestedRuleObjects, in _), "get cssText"),
                    null, JSPropertyAttributes.EnumerableConfigurableProperty);
            }
        }
        else if (trimmedRuleText.StartsWith("@font-face", StringComparison.OrdinalIgnoreCase))
        {
            // CSSFontFaceRule — type 5
            ruleObj.FastAddValue("type", new JSNumber(5), JSPropertyAttributes.EnumerableConfigurableValue);

            // Extract declarations from @font-face { ... }
            int braceOpen = ruleText.IndexOf('{');
            int braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var declarations = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var styleMap = DomBridge.ParseStyle(declarations);
                var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(styleMap, ruleObj);
                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText019Core(styleObj, in _), "get cssText"),
                    null, JSPropertyAttributes.EnumerableConfigurableProperty);
                ruleObj.FastAddValue("style", styleObj, JSPropertyAttributes.EnumerableConfigurableValue);
            }
        }
        else if (trimmedRuleText.StartsWith("@keyframes", StringComparison.OrdinalIgnoreCase))
        {
            // CSSKeyframesRule — type 7
            ruleObj.FastAddValue("type", new JSNumber(7), JSPropertyAttributes.EnumerableConfigurableValue);

            int braceOpen = ruleText.IndexOf('{');
            int braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var name = ruleText[10..braceOpen].Trim().Trim('"', '\'');
                var nestedCss = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var nestedRuleObjects = BuildNestedKeyframeObjects(nestedCss, nestedModelRules, parentStyleSheet, ruleObj);
                var nestedCssRules = BuildCssRuleListObject(
                    nestedRuleObjects,
                    rule => BuildCssKeyframeRuleObject(rule, parentStyleSheet, ruleObj));

                ruleObj.FastAddValue("name", new JSString(name), JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddValue("cssRules", nestedCssRules, JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText020Core(name, nestedRuleObjects, in _), "get cssText"),
                    null, JSPropertyAttributes.EnumerableConfigurableProperty);
            }
        }
        else if (trimmedRuleText.StartsWith("@property", StringComparison.OrdinalIgnoreCase))
        {
            // CSSPropertyRule — type 25
            ruleObj.FastAddValue("type", new JSNumber(25), JSPropertyAttributes.EnumerableConfigurableValue);

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

                ruleObj.FastAddValue("name", new JSString(propertyName), JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddValue("syntax", new JSString(syntax), JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddValue("inherits", inherits ? JSBoolean.True : JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddValue("initialValue",
                    string.IsNullOrEmpty(initialValue) ? JSNull.Value : new JSString(initialValue),
                    JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText021Core(inherits, initialValue, propertyName, syntax, in _), "get cssText"),
                    null, JSPropertyAttributes.EnumerableConfigurableProperty);
            }
        }
        else if (trimmedRuleText.StartsWith("@counter-style", StringComparison.OrdinalIgnoreCase))
        {
            // CSSCounterStyleRule — type 10
            ruleObj.FastAddValue("type", new JSNumber(10), JSPropertyAttributes.EnumerableConfigurableValue);

            var braceOpen = trimmedRuleText.IndexOf('{');
            var braceClose = trimmedRuleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var ruleName = trimmedRuleText[14..braceOpen].Trim();
                var descriptorsText = trimmedRuleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var descriptors = DomBridge.ParseStyle(descriptorsText);

                ruleObj.FastAddValue("name", new JSString(ruleName), JSPropertyAttributes.EnumerableConfigurableValue);

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
                    ruleObj.FastAddValue(jsName,
                        descriptors.TryGetValue(cssName, out var value) ? new JSString(value) : JSUndefined.Value,
                        JSPropertyAttributes.EnumerableConfigurableValue);
                }

                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText022Core(descriptorMap, ruleName, ruleObj, in _), "get cssText"),
                    null, JSPropertyAttributes.EnumerableConfigurableProperty);
            }
        }
        else if (trimmedRuleText.StartsWith("@supports", StringComparison.OrdinalIgnoreCase))
        {
            // CSSSupportsRule — type 11
            ruleObj.FastAddValue("type", new JSNumber(11), JSPropertyAttributes.EnumerableConfigurableValue);

            int braceOpen = ruleText.IndexOf('{');
            int braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var conditionText = ruleText[9..braceOpen].Trim();
                var nestedCss = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var nestedRuleObjects = BuildNestedRuleObjects(nestedCss, nestedModelRules, parentStyleSheet, ruleObj);
                var nestedCssRules = BuildCssRuleListObject(nestedRuleObjects,
                    rule => BuildCssRuleObject(rule, parentStyleSheet, ruleObj));

                ruleObj.FastAddValue("conditionText", new JSString(conditionText), JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddValue("cssRules", nestedCssRules, JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText023Core(conditionText, nestedRuleObjects, in _), "get cssText"),
                    null, JSPropertyAttributes.EnumerableConfigurableProperty);
            }
        }
        else if (trimmedRuleText.StartsWith("@layer", StringComparison.OrdinalIgnoreCase))
        {
            // CSSLayerRule — type 12
            ruleObj.FastAddValue("type", new JSNumber(12), JSPropertyAttributes.EnumerableConfigurableValue);

            var layerBody = ruleText[6..].Trim();
            var braceOpen = ruleText.IndexOf('{');
            var braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var nameText = ruleText[6..braceOpen].Trim();
                var nestedCss = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var nestedRuleObjects = BuildNestedRuleObjects(nestedCss, nestedModelRules, parentStyleSheet, ruleObj);
                var nestedCssRules = BuildCssRuleListObject(nestedRuleObjects,
                    rule => BuildCssRuleObject(rule, parentStyleSheet, ruleObj));

                ruleObj.FastAddValue("name",
                    string.IsNullOrEmpty(nameText) ? JSNull.Value : new JSString(nameText),
                    JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddValue("cssRules", nestedCssRules, JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText024Core(nameText, nestedRuleObjects, in _), "get cssText"),
                    null, JSPropertyAttributes.EnumerableConfigurableProperty);
            }
            else
            {
                var nameText = layerBody.TrimEnd(';').Trim();
                ruleObj.FastAddValue("name",
                    string.IsNullOrEmpty(nameText) ? JSNull.Value : new JSString(nameText),
                    JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddValue("cssRules", BuildCssRuleListObject([]), JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText025Core(nameText, in _), "get cssText"),
                    null, JSPropertyAttributes.EnumerableConfigurableProperty);
            }
        }
        else if (trimmedRuleText.StartsWith("@namespace", StringComparison.OrdinalIgnoreCase))
        {
            // CSSNamespaceRule — type 9
            ruleObj.FastAddValue("type", new JSNumber(9), JSPropertyAttributes.EnumerableConfigurableValue);

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

            ruleObj.FastAddValue("namespaceURI", new JSString(namespaceUri), JSPropertyAttributes.EnumerableConfigurableValue);
            ruleObj.FastAddValue("prefix",
                string.IsNullOrEmpty(prefix) ? JSUndefined.Value : new JSString(prefix),
                JSPropertyAttributes.EnumerableConfigurableValue);
            ruleObj.FastAddProperty("cssText",
                new DomFunction((in _) => JsStyleSheetsGetCssText026Core(namespaceUri, prefix, in _), "get cssText"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);
        }
        else if (trimmedRuleText.StartsWith("@page", StringComparison.OrdinalIgnoreCase))
        {
            // CSSPageRule — type 6
            ruleObj.FastAddValue("type", new JSNumber(6), JSPropertyAttributes.EnumerableConfigurableValue);

            var braceOpen = ruleText.IndexOf('{');
            var braceClose = ruleText.LastIndexOf('}');
            if (braceOpen >= 0 && braceClose > braceOpen)
            {
                var selectorText = ruleText[5..braceOpen].Trim();
                var declarations = ruleText.Substring(braceOpen + 1, braceClose - braceOpen - 1).Trim();
                var styleMap = DomBridge.ParseStyle(declarations);
                var styleObj = StyleDeclarationBinding.BuildRuleDeclaration(styleMap, ruleObj);

                ruleObj.FastAddValue("selectorText", new JSString(selectorText), JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddValue("style", styleObj, JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText027Core(selectorText, styleObj, in _), "get cssText"),
                    null, JSPropertyAttributes.EnumerableConfigurableProperty);
            }
        }
        else
        {
            // CSSStyleRule — type 1
            ruleObj.FastAddValue("type", new JSNumber(1), JSPropertyAttributes.EnumerableConfigurableValue);

            // Extract selector text
            int braceOpen = ruleText.IndexOf('{');
            if (braceOpen >= 0)
            {
                var selectorText = ruleText[..braceOpen].Trim();
                ruleObj.FastAddValue("selectorText", new JSString(selectorText), JSPropertyAttributes.EnumerableConfigurableValue);
                ruleObj.FastAddProperty("cssText",
                    new DomFunction((in _) => JsStyleSheetsGetCssText028Core(ruleObj, selectorText, in _), "get cssText"),
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
        }

        return ruleObj;
    }

}
