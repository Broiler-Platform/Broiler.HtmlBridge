using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

/// <summary>
/// CSSOM — the <c>document.styleSheets</c> collection and the individual
/// <c>CSSStyleSheet</c> objects (per-element identity cache, the live <c>cssRules</c>
/// collection, and <c>insertRule</c>/<c>deleteRule</c> mutation bookkeeping). The
/// <c>CSSRuleList</c>/<c>CSSRule</c> object model and the <c>JsStyleSheets*Core</c> callbacks
/// this builds on live in the <see cref="Dom.Features.StyleSheetBinding"/> feature module
/// (Phase 3, P3.15).
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>Cache for stylesheet objects, keyed by the owning style element.</summary>
    private readonly Dictionary<DomElement, JSObject> _styleSheetCache = [];

    /// <summary>
    /// Whether the element has an associated CSS style sheet, and so belongs in a document's
    /// <c>styleSheets</c> collection (CSSOM §2.2).
    /// </summary>
    /// <remarks>
    /// Shared with the main-document binding through
    /// <see cref="Dom.Features.IDocumentCollectionHost.HasAssociatedStyleSheet"/>. It is factored
    /// out precisely because the two collections disagreed: this one has always counted
    /// <c>&lt;link rel=stylesheet&gt;</c>, and the main document's filtered to tag <c>style</c>, so
    /// the same tree answered two different things depending on which document was asked.
    /// <para>
    /// A disabled <c>&lt;link&gt;</c> has no associated sheet, so it is absent (HTML §4.2.4
    /// <c>&lt;link disabled&gt;</c>). A <c>&lt;style&gt;</c> whose sheet was disabled through CSSOM
    /// (<c>CSSStyleSheet.disabled</c>) still appears — only its rules stop applying — so it is not
    /// filtered here.
    /// </para>
    /// </remarks>
    private bool HasAssociatedStyleSheet(DomElement element)
    {
        bool isStyle = string.Equals(element.TagName, "style", StringComparison.OrdinalIgnoreCase);
        if (!isStyle && !IsExternalStylesheet(element))
            return false;

        return !(!isStyle && IsStyleSheetDisabled(element));
    }

    /// <summary>
    /// The effective CSSOM <c>disabled</c> state of a <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c>
    /// stylesheet: the script-set <c>CSSStyleSheet.disabled</c> flag when present, otherwise
    /// the element's <c>disabled</c> content attribute (only a <c>&lt;link&gt;</c> carries one —
    /// <c>HTMLLinkElement.disabled</c> reflects it). A disabled sheet does not apply to the
    /// cascade (CSSOM §2.3).
    /// </summary>
    private bool IsStyleSheetDisabled(DomElement element)
    {
        var state = StyleSheetStateFor(element);
        if (state.DisabledOverride is bool overridden)
            return overridden;

        return string.Equals(element.TagName, "link", StringComparison.OrdinalIgnoreCase)
            && HasAttr(element, "disabled");
    }

    /// <summary>
    /// Sets the script-driven <c>CSSStyleSheet.disabled</c> flag on a stylesheet element and
    /// re-runs the cascade so a newly (un)disabled sheet (dis)appears from computed style.
    /// Does not touch the <c>disabled</c> content attribute — that is only reflected by
    /// <c>HTMLLinkElement.disabled</c>, not by <c>CSSStyleSheet.disabled</c>.
    /// </summary>
    private void SetStyleSheetDisabledFlag(DomElement element, bool value)
    {
        StyleSheetStateFor(element).DisabledOverride = value;
        InvalidateStyleScope(element);
    }

    /// <summary>
    /// Returns <c>true</c> if the element is a <c>&lt;link rel="stylesheet" href="..."&gt;</c>.
    /// </summary>
    private static bool IsExternalStylesheet(DomElement element)
    {
        if (!string.Equals(element.TagName, "link", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryGetAttribute(element, "rel", out var rel) ||
            !rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase))
            return false;
        return HasAttr(element, "href");
    }

    /// <summary>
    /// The CSSOM <c>StyleSheet.href</c> value for a style element: the linked sheet's location,
    /// resolved the same way the sheet itself is read (<see cref="ResolveStyleSheetLinkUrl"/>), so
    /// the URL a script reads is the URL the rules came from — including under a
    /// <c>&lt;base href&gt;</c>. An inline <c>&lt;style&gt;</c>, and a <c>&lt;link&gt;</c> with a
    /// blank href, have no location and answer <c>null</c>.
    /// </summary>
    private JSValue StyleSheetHrefValue(DomElement element) =>
        IsExternalStylesheet(element) &&
        TryGetAttribute(element, "href", out var href) &&
        !string.IsNullOrWhiteSpace(href)
            ? new JSString(ResolveStyleSheetLinkUrl(href))
            : JSNull.Value;

    /// <summary>
    /// Builds a CSSStyleSheet JSObject for a style element.
    /// Cached per style element to ensure identity (the same object is returned
    /// each time, making cssRules a live collection per the CSSOM spec).
    /// </summary>
    private JSObject BuildStyleSheetObject(DomElement styleElement)
    {
        if (_styleSheetCache.TryGetValue(styleElement, out var cached))
            return cached;

        var sheet = new JSObject();

        // ownerNode
        sheet.FastAddProperty("ownerNode", new DomFunction((in _) => ToJSObject(styleElement), "get ownerNode"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // href — CSSOM §2.1 StyleSheet.href: the location of the sheet, null for an inline
        // <style>. It was null for a linked sheet too, so a <link> presented itself in
        // document.styleSheets as an inline sheet that happened to have no rules. A live getter
        // rather than a captured value: the sheet object is cached per element for identity, and a
        // script can re-point the link at another href afterwards.
        sheet.FastAddProperty("href",
            new DomFunction((in _) => StyleSheetHrefValue(styleElement), "get href"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // disabled — CSSOM StyleSheet.disabled. A true value prevents the sheet from
        // applying (CSSOM §2.3). Getting reads the effective state (script flag, else the
        // <link disabled> content attribute); setting stores the script flag and re-cascades.
        sheet.FastAddProperty("disabled",
            new DomFunction((in _) => IsStyleSheetDisabled(styleElement) ? JSBoolean.True : JSBoolean.False, "get disabled"),
            new DomFunction((in a) => { SetStyleSheetDisabledFlag(styleElement, a.Length > 0 && a[0].BooleanValue); return JSUndefined.Value; }, "set disabled"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // Internal rules storage for this stylesheet — the single shared, mutable
        // Broiler.CSS rule model held in the element's runtime state (Phase 6 store
        // unification). The same list backs the renderer text and the
        // getComputedStyle engine, so a script insertRule/deleteRule here is observed
        // by both. CurrentRules() reparses on textContent change before returning it.
        List<CssRule> CurrentRules() => EnsureStyleSheetRulesCurrent(styleElement);
        void MarkRulesMutated() => StyleSheetStateFor(styleElement).RulesMutated = true;

        // Live cssRules object — single instance that always reflects current state
        var liveCssRules = new JSObject();
        var lastSyncedRuleCount = 0;
        // length is a live getter that always reflects the current rule count
        liveCssRules.FastAddProperty("length",
            new DomFunction((in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetLength002Core(CurrentRules, in _), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        liveCssRules.FastAddValue("item",
            new DomFunction((in a) => Dom.Features.StyleSheetBinding.JsStyleSheetsItem003Core(SyncLiveCssRulesIndices, liveCssRules, CurrentRules, in a), "item", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // Syncs indexed properties on the live cssRules object with the shared model
        void SyncLiveCssRulesIndices()
        {
            var rules = CurrentRules();
            for (var i = 0; i < rules.Count; i++)
            {
                var ruleObj = Dom.Features.StyleSheetBinding.BuildCssRuleObject(rules[i], sheet);
                liveCssRules[(uint)i] = ruleObj;
            }

            for (var i = rules.Count; i < lastSyncedRuleCount; i++)
                liveCssRules.GetElements().RemoveAt((uint)i);

            lastSyncedRuleCount = rules.Count;
        }

        // cssRules — returns the live collection, syncing indices on access
        sheet.FastAddProperty("cssRules",
            new DomFunction((in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetCssRules004Core(SyncLiveCssRulesIndices, liveCssRules, in _), "get cssRules"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // insertRule(rule, index) — mutates the shared model (marking it mutated so
        // the renderer/engine serialize from it) and resyncs the live collection
        sheet.FastAddValue("insertRule",
            new DomFunction((in a) => Dom.Features.StyleSheetBinding.JsStyleSheetsInsertRule005Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in a), "insertRule", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // deleteRule(index) — removes a rule from the shared model
        sheet.FastAddValue("deleteRule",
            new DomFunction((in a) => Dom.Features.StyleSheetBinding.JsStyleSheetsDeleteRule006Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in a), "deleteRule", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        _styleSheetCache[styleElement] = sheet;
        return sheet;
    }

}
