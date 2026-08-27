using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;
using Broiler.CSS;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// Constructable stylesheets (CSSOM) — <c>new CSSStyleSheet()</c>, its
/// <c>insertRule</c>/<c>deleteRule</c>/<c>replaceSync</c>/<c>replace</c> surface, and
/// <c>document.adoptedStyleSheets</c>. A constructed sheet is not tied to a
/// <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> element; it carries its own rule list and applies
/// to the document only while it is in <c>adoptedStyleSheets</c>. At serialization the adopted
/// sheets are emitted as synthetic <c>&lt;style&gt;</c> elements appended after the document's
/// own stylesheets, so the renderer applies them in the correct cascade order (the WPT
/// <c>css/cssom</c> constructable family, e.g.
/// <c>CSSStyleSheet-constructable-insertRule-base-uri</c>, which adopts a sheet whose rule sets
/// a <c>background-image</c>).
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>The live <c>document.adoptedStyleSheets</c> array (constructed sheets in
    /// application order). Lazily created; null until first read.</summary>
    private JSArray? _adoptedStyleSheets;

    /// <summary>Rule list backing each constructed <c>CSSStyleSheet</c> object, so the
    /// serialization pass can emit the adopted sheets' rules.</summary>
    private readonly Dictionary<JSObject, List<CssRule>> _constructedSheetRules = [];

    /// <summary><c>new CSSStyleSheet(options)</c> — an empty constructed stylesheet. Options
    /// (media/disabled/baseURL) are accepted but not yet modelled; a constructed sheet's rules
    /// resolve relative <c>url()</c>s against the document base at render time.</summary>
    private JSValue CreateConstructedStyleSheet(in Arguments a)
    {
        var rules = new List<CssRule>();
        return BuildConstructedStyleSheetObject(rules);
    }

    private JSObject BuildConstructedStyleSheetObject(List<CssRule> rules)
    {
        var sheet = new JSObject();
        _constructedSheetRules[sheet] = rules;

        List<CssRule> CurrentRules() => rules;
        // A constructed sheet has no owner element and so no StyleSheetRuntimeState to mark — there
        // is nothing to reparse from, the list *is* the sheet. It still reaches the cascade through
        // adoptedStyleSheets, so an insertRule/deleteRule on it is a layout change no DOM mutation
        // records: move the epoch so a retained geometry snapshot is not answered from the pre-edit
        // rules. See BridgeRuntimeStateEpoch.
        static void MarkRulesMutated() => BridgeRuntimeStateEpoch.Bump();

        // ownerNode is null for a constructed sheet; href is null (no source URL).
        sheet.FastAddProperty("ownerNode", NullFunction("get ownerNode"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        sheet.FastAddProperty("href", NullFunction("get href"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // disabled — a script flag on the sheet; a disabled adopted sheet does not apply.
        var disabled = false;
        sheet.FastAddProperty("disabled",
            new DomFunction((in _) => disabled ? JSBoolean.True : JSBoolean.False, "get disabled"),
            new DomFunction((in a) => { disabled = a.Length > 0 && a[0].BooleanValue; return JSUndefined.Value; }, "set disabled"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        var liveCssRules = new JSObject();
        var lastSyncedRuleCount = 0;
        liveCssRules.FastAddProperty("length",
            new DomFunction((in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetLength002Core(CurrentRules, in _), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        liveCssRules.FastAddValue("item",
            new DomFunction((in a) => Dom.Features.StyleSheetBinding.JsStyleSheetsItem003Core(SyncLiveCssRulesIndices, liveCssRules, CurrentRules, in a), "item", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        void SyncLiveCssRulesIndices()
        {
            var current = CurrentRules();
            for (var i = 0; i < current.Count; i++)
                liveCssRules[(uint)i] = Dom.Features.StyleSheetBinding.BuildCssRuleObject(current[i], sheet);

            for (var i = current.Count; i < lastSyncedRuleCount; i++)
                liveCssRules.GetElements().RemoveAt((uint)i);

            lastSyncedRuleCount = current.Count;
        }

        sheet.FastAddProperty("cssRules",
            new DomFunction((in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetCssRules004Core(SyncLiveCssRulesIndices, liveCssRules, in _), "get cssRules"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        sheet.FastAddValue("insertRule",
            new DomFunction((in a) => Dom.Features.StyleSheetBinding.JsStyleSheetsInsertRule005Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in a), "insertRule", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);
        sheet.FastAddValue("deleteRule",
            new DomFunction((in a) => Dom.Features.StyleSheetBinding.JsStyleSheetsDeleteRule006Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in a), "deleteRule", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // replaceSync(text) — replace all rules from a CSS string (any @import is dropped per
        // spec). replace(text) does the same and returns an already-resolved promise of the sheet.
        void ReplaceFromText(string text)
        {
            rules.Clear();
            foreach (var rule in new CssParser().ParseStyleSheet(text).Rules)
            {
                if (rule is CssAtRule at && at.Name.Equals("import", StringComparison.OrdinalIgnoreCase))
                    continue;
                rules.Add(rule);
            }
            SyncLiveCssRulesIndices();
        }

        sheet.FastAddValue("replaceSync",
            new DomFunction((in a) => { ReplaceFromText(a.Length > 0 ? a[0].ToString() : string.Empty); return JSUndefined.Value; }, "replaceSync", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        sheet.FastAddValue("replace",
            new DomFunction((in a) => { ReplaceFromText(a.Length > 0 ? a[0].ToString() : string.Empty); return ResolvedThenableWith(sheet); }, "replace", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return sheet;
    }

    /// <summary>An already-resolved thenable that yields <paramref name="value"/> — the
    /// promise <c>CSSStyleSheet.replace()</c> returns (resolving with the sheet itself).</summary>
    private static JSObject ResolvedThenableWith(JSValue value)
    {
        var thenable = new JSObject();
        JSValue Then(in Arguments args)
        {
            if (args.Length > 0 && args[0] is JSFunction cb)
            {
                try { cb.InvokeFunction(new Arguments(cb, value)); }
                catch { /* a replace().then callback must not abort the render */ }
            }
            return thenable;
        }
        thenable.FastAddValue("then", new DomFunction(Then, "then", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        thenable.FastAddValue("catch", new DomFunction((in _) => thenable, "catch", 1), JSPropertyAttributes.EnumerableConfigurableValue);
        thenable.FastAddValue("finally",
            new DomFunction((in a) => { if (a.Length > 0 && a[0] is JSFunction cb) { try { cb.InvokeFunction(new Arguments(cb)); } catch { } } return thenable; }, "finally", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        return thenable;
    }

    /// <summary>The live <c>document.adoptedStyleSheets</c> array, created on first access.</summary>
    private JSArray AdoptedStyleSheetsArray() => _adoptedStyleSheets ??= new JSArray();

    /// <summary>Replaces <c>document.adoptedStyleSheets</c> with the assigned array's members
    /// (<c>document.adoptedStyleSheets = [sheet, …]</c>); <c>.push()</c> on the getter's array
    /// is handled directly by the returned array.</summary>
    private void SetAdoptedStyleSheets(JSValue value)
    {
        var replacement = new JSArray();
        if (value is JSArray array)
            foreach (var (_, item) in array.GetArrayElements(withHoles: false))
                replacement.Add(item);

        _adoptedStyleSheets = replacement;
    }

    /// <summary>
    /// Emits each adopted stylesheet as a synthetic <c>&lt;style&gt;</c> appended after the
    /// document's own stylesheets, so the renderer applies the adopted rules in cascade order.
    /// A no-op when nothing is adopted. Runs from <see cref="ApplySerializationTransforms"/>.
    /// </summary>
    private void ApplyAdoptedStyleSheets(DomElement root)
    {
        if (_adoptedStyleSheets is null || _adoptedStyleSheets.Length == 0)
            return;

        var head = FindFirstElementByTagName(root, "head");
        var container = head ?? root;

        foreach (var (_, item) in _adoptedStyleSheets.GetArrayElements(withHoles: false))
        {
            if (item is not JSObject sheetObj ||
                !_constructedSheetRules.TryGetValue(sheetObj, out var rules) ||
                rules.Count == 0)
                continue;

            if (sheetObj[(KeyString)"disabled"] is JSValue d && d.BooleanValue)
                continue;

            var css = string.Join("\n", rules.Select(CssSerializer.Serialize));
            var styleElement = CreateBridgeElement("style");
            SetElementTextContent(styleElement, css);
            SetParent(styleElement, container);
            container.AppendChild(styleElement);
        }
    }
}
