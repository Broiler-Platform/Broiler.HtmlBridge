using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

// Engine-typed only for the BuildStyleSheetObject adapter at the foot of this file, whose return type is
// fixed by the unmigrated IDocumentCollectionHost / ISubDocumentHost contracts (neither owned this round).
using Broiler.JavaScript.Runtime;

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
    private readonly Dictionary<DomElement, JsValue> _styleSheetCache = [];

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
    private JsValue StyleSheetHrefValue(DomElement element) =>
        IsExternalStylesheet(element) &&
        TryGetAttribute(element, "href", out var href) &&
        !string.IsNullOrWhiteSpace(href)
            ? JsValue.String(ResolveStyleSheetLinkUrl(href))
            : JsValue.Null;

    /// <summary>
    /// The <c>CSSStyleSheet</c> for a style element, for a caller that still holds engine objects.
    /// </summary>
    /// <remarks>
    /// The adapter that keeps <c>Features/IDocumentCollectionHost.cs</c>,
    /// <c>Features/ISubDocumentHost.cs</c> and their implementations compiling untouched — none is owned
    /// this round, and all three declare this return type. It is a cast and not a conversion (see
    /// <see cref="Dom.Runtime.JsInterop"/>), so the object handed over is the cached one and sheet
    /// identity is the same question it was.
    /// </remarks>
    private JSObject BuildStyleSheetObject(DomElement styleElement) =>
        Dom.Runtime.JsInterop.ToEngineObject(BuildStyleSheet(styleElement));

    /// <summary>
    /// Builds a CSSStyleSheet object for a style element.
    /// Cached per style element to ensure identity (the same object is returned
    /// each time, making cssRules a live collection per the CSSOM spec).
    /// </summary>
    private JsValue BuildStyleSheet(DomElement styleElement)
    {
        if (_styleSheetCache.TryGetValue(styleElement, out var cached))
            return cached;

        var realm = Realm;
        var sheet = realm.NewObject();

        // ownerNode — the wrapper factory keeps its engine-shaped name because DomBridge/Utilities.cs,
        // which owns the wrapper cache, has not migrated; the handle over what it returns is the same
        // object, so sheet.ownerNode === el still holds.
        realm.DefineAccessor(sheet, "ownerNode",
            (in _) => Dom.Runtime.JsInterop.FromEngineObject(ToJSObject(styleElement)), null);

        // href — CSSOM §2.1 StyleSheet.href: the location of the sheet, null for an inline
        // <style>. It was null for a linked sheet too, so a <link> presented itself in
        // document.styleSheets as an inline sheet that happened to have no rules. A live getter
        // rather than a captured value: the sheet object is cached per element for identity, and a
        // script can re-point the link at another href afterwards.
        realm.DefineAccessor(sheet, "href", (in _) => StyleSheetHrefValue(styleElement), null);

        // disabled — CSSOM StyleSheet.disabled. A true value prevents the sheet from
        // applying (CSSOM §2.3). Getting reads the effective state (script flag, else the
        // <link disabled> content attribute); setting stores the script flag and re-cascades.
        realm.DefineAccessor(sheet, "disabled",
            (in _) => JsValue.Boolean(IsStyleSheetDisabled(styleElement)),
            (in call) =>
            {
                SetStyleSheetDisabledFlag(styleElement, call.Length > 0 && call[0].AsBoolean);
                return JsValue.Undefined;
            });

        // Internal rules storage for this stylesheet — the single shared, mutable
        // Broiler.CSS rule model held in the element's runtime state (Phase 6 store
        // unification). The same list backs the renderer text and the
        // getComputedStyle engine, so a script insertRule/deleteRule here is observed
        // by both. CurrentRules() reparses on textContent change before returning it.
        List<CssRule> CurrentRules() => EnsureStyleSheetRulesCurrent(styleElement);
        void MarkRulesMutated() => StyleSheetStateFor(styleElement).RulesMutated = true;

        // Live cssRules object — single instance that always reflects current state
        var liveCssRules = realm.NewObject();
        var lastSyncedRuleCount = 0;
        // length is a live getter that always reflects the current rule count
        realm.DefineAccessor(liveCssRules, "length",
            (in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetLength002Core(CurrentRules), null);

        realm.DefineValue(liveCssRules, "item",
            realm.NewMethod("item",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsItem003Core(SyncLiveCssRulesIndices, liveCssRules, CurrentRules, in call), 1));

        // Syncs indexed properties on the live cssRules object with the shared model
        void SyncLiveCssRulesIndices()
        {
            var rules = CurrentRules();
            for (var i = 0; i < rules.Count; i++)
            {
                var ruleObj = Dom.Features.StyleSheetBinding.BuildCssRuleObject(realm, rules[i], sheet);
                realm.DefineIndex(liveCssRules, (uint)i, ruleObj);
            }

            // Retiring an index is the one CSSOM operation JSEAL cannot express; see
            // StyleSheetBinding.RetireIndex, which is where the reasoning lives.
            for (var i = rules.Count; i < lastSyncedRuleCount; i++)
                Dom.Features.StyleSheetBinding.RetireIndex(liveCssRules, (uint)i);

            lastSyncedRuleCount = rules.Count;
        }

        // cssRules — returns the live collection, syncing indices on access
        realm.DefineAccessor(sheet, "cssRules",
            (in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetCssRules004Core(SyncLiveCssRulesIndices, liveCssRules), null);

        // insertRule(rule, index) — mutates the shared model (marking it mutated so
        // the renderer/engine serialize from it) and resyncs the live collection
        realm.DefineValue(sheet, "insertRule",
            realm.NewMethod("insertRule",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsInsertRule005Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in call), 2));

        // deleteRule(index) — removes a rule from the shared model
        realm.DefineValue(sheet, "deleteRule",
            realm.NewMethod("deleteRule",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsDeleteRule006Core(CurrentRules, MarkRulesMutated, SyncLiveCssRulesIndices, in call), 1));

        _styleSheetCache[styleElement] = sheet;
        return sheet;
    }

}
