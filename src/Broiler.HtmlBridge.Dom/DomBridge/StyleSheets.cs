using System.Linq;
using System.Runtime.CompilerServices;
using Broiler.CSS;
using Broiler.CSS.Dom;
using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// CSSOM — the <c>document.styleSheets</c> collection and the individual
/// <c>CSSStyleSheet</c> objects (per-element identity cache, the live <c>cssRules</c>
/// collection, and <c>insertRule</c>/<c>deleteRule</c> mutation bookkeeping). The
/// <c>CSSRuleList</c>/<c>CSSRule</c> object model and the <c>JsStyleSheets*Core</c> callbacks
/// this builds on live in the <see cref="Dom.Features.StyleSheetBinding"/> feature module.
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

        // ownerNode — the element's wrapper from WrapNode, the handle JsObjectRegistry caches, so
        // sheet.ownerNode === el holds.
        realm.DefineAccessor(sheet, "ownerNode",
            (in _) => WrapNode(styleElement), null);

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
        // Broiler.CSS rule model held in the element's runtime state. The same list backs the
        // renderer text and the
        // getComputedStyle engine, so a script insertRule/deleteRule here, or a write to a
        // style rule's style (ruleModel), is observed by both. CurrentRules() reparses on
        // textContent change before returning it; MarkRulesMutated is every edit's one report,
        // and also invalidates computed style (OnStyleSheetRulesMutated says why it must).
        List<CssRule> CurrentRules() => EnsureStyleSheetRulesCurrent(styleElement);
        void MarkRulesMutated() => OnStyleSheetRulesMutated(styleElement);
        var ruleModel = new Dom.Features.StyleSheetRuleModel(CurrentRules, MarkRulesMutated);

        // Live cssRules object — single instance that always reflects current state
        var liveCssRules = realm.NewObject();
        var lastSyncedRuleCount = 0;
        // length is a live getter that always reflects the current rule count
        realm.DefineAccessor(liveCssRules, "length",
            (in _) => Dom.Features.StyleSheetBinding.JsStyleSheetsGetLength002Core(CurrentRules), null);

        realm.DefineValue(liveCssRules, "item",
            realm.NewMethod("item",
                (in call) => Dom.Features.StyleSheetBinding.JsStyleSheetsItem003Core(SyncLiveCssRulesIndices, liveCssRules, CurrentRules, in call), 1));

        // Syncs indexed properties on the live cssRules object with the CSSOM view of the shared model
        void SyncLiveCssRulesIndices()
        {
            var rules = Dom.Features.StyleSheetBinding.CssomRules(CurrentRules());
            for (var i = 0; i < rules.Count; i++)
            {
                var ruleObj = Dom.Features.StyleSheetBinding.BuildCssRuleObject(realm, rules[i], sheet, default, ruleModel);
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
        // the renderer/engine serialize from it and computed style is re-resolved) and
        // resyncs the live collection
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

/// <summary>
/// Render-time <c>&lt;base href&gt;</c> resolution for stylesheet <c>url()</c> references.
/// The renderer resolves a relative CSS <c>url()</c> against a single document base URL and
/// never consults the <c>&lt;base&gt;</c> element, so a page that sets
/// <c>&lt;base href="/images/"&gt;</c> and then references <c>url(green.png)</c> resolved the
/// image against the document's own directory instead of the base — the resource was not
/// found and the fallback colour showed through (the WPT <c>css/…/*base-uri*</c> family, e.g.
/// <c>css-values/inline-cache-base-uri-cssom</c>).
/// <para>
/// This transform rewrites relative <c>url()</c> references in every <c>&lt;style&gt;</c>
/// against the document's first <c>&lt;base href&gt;</c> so they reach the renderer already
/// resolved. A root-relative base (<c>/images/</c>) keeps the resolved URL root-relative, so a
/// host-relative resource mapper (the WPT <c>wptRoot</c> handler, which serves <c>/x</c> from
/// the test root) still recognises it; an absolute base resolves to an absolute URL.
/// </para>
/// <para>
/// Runs inside <see cref="ApplySerializationTransforms"/> after
/// <see cref="InlineStyleSheetImports(DomElement)"/> (so inlined <c>@import</c> content — already rebased
/// onto the imported sheet's own URL — carries absolute <c>url()</c>s this pass leaves alone).
/// Only the render-bound serialization is affected; the live CSSOM rule model and JS-visible
/// serialization are untouched. <c>&lt;style&gt;</c> <c>url()</c>s and
/// <c>&lt;link rel="stylesheet"&gt;</c> <c>href</c>s are rebased against <c>&lt;base&gt;</c>;
/// other element <c>src</c>/<c>href</c> attributes still resolve without <c>&lt;base&gt;</c>.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Rewrites relative <c>url()</c> references in every <c>&lt;style&gt;</c> in the tree
    /// against the document's first <c>&lt;base href&gt;</c>, and likewise resolves each
    /// <c>&lt;link rel="stylesheet"&gt;</c>'s relative <c>href</c> so the linked sheet is
    /// fetched from the base-relative location rather than the document's own directory. A
    /// no-op (byte-identical output) when the document declares no usable <c>&lt;base href&gt;</c>.
    /// </summary>
    private void ApplyBaseHrefToStyleUrls(DomElement root)
    {
        if (!TryFindDocumentBaseHref(root, out var baseHref))
            return;

        RewriteStyleElementUrls(root, baseHref);
        RewriteLinkStyleSheetHrefs(root, baseHref);
    }

    /// <summary>
    /// Resolves the relative <c>href</c> of every <c>&lt;link rel="stylesheet"&gt;</c> against
    /// the document's first <c>&lt;base href&gt;</c>. The renderer resolves a linked sheet's
    /// <c>href</c> against the document URL and never consults <c>&lt;base&gt;</c>, so
    /// <c>&lt;base href="resources/"&gt;</c> before <c>&lt;link href="stylesheet.css"&gt;</c>
    /// fetched the sheet from the document's own directory instead of <c>resources/</c> — it
    /// was not found and the unstyled fallback showed through (WPT
    /// <c>html/semantics/document-metadata/the-link-element/stylesheet-with-base</c>). Absolute,
    /// <c>data:</c>, root-relative, and fragment hrefs are left untouched.
    /// </summary>
    private void RewriteLinkStyleSheetHrefs(DomElement root, string baseHref)
    {
        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (!element.TagName.Equals("link", StringComparison.OrdinalIgnoreCase) ||
                !LinkRelIsStyleSheet(element) ||
                !TryGetAttribute(element, "href", out var href) ||
                string.IsNullOrWhiteSpace(href))
                continue;

            var resolved = ResolveUrlAgainstBaseHref(href, baseHref);
            if (resolved is not null)
                SetAttr(element, "href", resolved);
        }
    }

    /// <summary>The first <c>&lt;base&gt;</c> in document order with a non-empty
    /// <c>href</c> — the element that sets the document base URL (HTML §4.2.3).</summary>
    private static bool TryFindDocumentBaseHref(DomElement root, out string baseHref)
    {
        if (HtmlDocumentQueries.TryGetEffectiveBaseHref(root, out var href))
        {
            baseHref = href;
            return true;
        }

        baseHref = string.Empty;
        return false;
    }

    private void RewriteStyleElementUrls(DomElement element, string baseHref)
    {
        if (!IsText(element) &&
            element.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            var original = GetStyleElementCssText(element);
            var rewritten = RewriteCssUrlsAgainstBaseHref(original, baseHref);
            if (!string.Equals(rewritten, original, StringComparison.Ordinal))
                element.TextContent = rewritten;
        }

        foreach (var child in ChildElements(element))
            RewriteStyleElementUrls(child, baseHref);
    }

    /// <summary>Rewrites each relative <c>url(...)</c> in <paramref name="css"/> to its
    /// resolution against <paramref name="baseHref"/>; absolute, <c>data:</c>, and fragment
    /// references are left byte-identical.</summary>
    private string RewriteCssUrlsAgainstBaseHref(string css, string baseHref)
        => UrlFunctionPattern.Replace(css, match =>
        {
            var resolved = ResolveUrlAgainstBaseHref(match.Groups[2].Value, baseHref);
            return resolved is null ? match.Value : $"url(\"{resolved}\")";
        });

    /// <summary>
    /// Resolves a CSS <c>url()</c> value or a <c>&lt;link&gt;</c> href against a
    /// <c>&lt;base href&gt;</c>. Delegates to <see cref="HtmlBaseHref.Resolve"/>, the shared
    /// seam this and the WPT runner's stylesheet inliner both go through — see that type for
    /// the resolution rules and why there is only one implementation.
    /// </summary>
    private string? ResolveUrlAgainstBaseHref(string rawUrl, string baseHref) =>
        HtmlBaseHref.Resolve(rawUrl, baseHref, _pageUrl);

    /// <summary>
    /// The URL a <c>&lt;link rel="stylesheet"&gt;</c>'s sheet is read from: a <c>data:</c> href
    /// verbatim, anything else resolved against the <em>document base URL</em>. The verbatim case
    /// matters — round tripping a data: URL through <see cref="Uri"/> normalizes the percent-escapes
    /// its payload is made of, and the payload is the stylesheet.
    /// <para>
    /// The base is the document's, not the page's: a <c>&lt;base href&gt;</c> relocates a linked
    /// sheet (HTML §4.2.3), which is what the render-bound
    /// <see cref="RewriteLinkStyleSheetHrefs"/> pass already honours on the serialization
    /// projection. Resolving against the page URL here instead would read a *different* sheet than
    /// the one that paints whenever the document declares a base.
    /// </para>
    /// </summary>
    private string ResolveStyleSheetLinkUrl(string href) =>
        href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? href
            : ResolveAgainstDocumentBaseUrl(href);

    /// <summary>
    /// Resolves a content-attribute URL against the document base URL — the document's first
    /// <c>&lt;base href&gt;</c> resolved against the page URL when it declares one, the page URL
    /// otherwise. Leaves the value untouched when neither is usable as a base.
    /// </summary>
    private string ResolveAgainstDocumentBaseUrl(string url) =>
        Uri.TryCreate(DocumentBaseUrl(), UriKind.Absolute, out var baseUri) &&
        Uri.TryCreate(baseUri, url, out var resolved)
            ? resolved.AbsoluteUri
            : url;

    private ulong _documentBaseUrlVersion;
    private string? _documentBaseUrlCache;

    /// <summary>
    /// The document base URL, cached against <see cref="DomDocument.Version"/>.
    /// </summary>
    /// <remarks>
    /// Finding the <c>&lt;base href&gt;</c> means walking every descendant, and the overwhelming
    /// majority of documents declare none — the same reason
    /// <see cref="InlineStyleSheetImports(DomElement)"/> resolves its base lazily and at most once.
    /// Here the callers are per-<c>&lt;link&gt;</c> rather than per-document, so without a cache a
    /// page with many sheets pays that walk once for each of them. The version counter is bumped by
    /// every tree edit and attribute write, so adding, removing or re-pointing a <c>&lt;base&gt;</c>
    /// invalidates this on the next call.
    /// </remarks>
    private string DocumentBaseUrl()
    {
        var version = _document.Version;
        if (_documentBaseUrlCache is null || _documentBaseUrlVersion != version)
        {
            _documentBaseUrlCache = HtmlBaseHref.ResolveDocumentBaseUrl(
                _pageUrl, TryFindDocumentBaseHref(DocumentElement, out var baseHref) ? baseHref : null);
            _documentBaseUrlVersion = version;
        }

        return _documentBaseUrlCache;
    }
}

/// <summary>
/// External stylesheets: the <c>style-src</c> gate a <c>&lt;link rel="stylesheet"&gt;</c> passes before it is
/// fetched (the same request-level check an <c>@import</c> passes, in StyleSheets.Imports.cs), the prefetch and
/// fetch themselves, and the link's <c>load</c> event.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether the document's Content Security Policy permits fetching and applying an external stylesheet
    /// from <paramref name="href"/> (a <c>&lt;link rel="stylesheet"&gt;</c>, whose own <c>nonce</c> attribute is
    /// the request's nonce). When no policy is configured every stylesheet is allowed. Mirrors the script-side
    /// <see cref="ContentSecurityPolicy.AllowsExternalScript"/> gate so DOM/CSS never fetch or apply a
    /// <c>style-src</c>-blocked external stylesheet (CSP stays in the host layer; DOM and CSS
    /// receive already-authorised content).
    /// </summary>
    private bool IsExternalStyleAllowedByCsp(DomElement linkEl, string href) =>
        IsStyleFetchAllowedByCsp(href, TryGetAttribute(linkEl, "nonce", out var nonce) ? nonce : null);

    /// <summary>
    /// The check behind <see cref="IsExternalStyleAllowedByCsp"/>, for a stylesheet request that has no element
    /// to take a nonce from: whether <c>style-src-elem</c> → <c>style-src</c> → <c>default-src</c> admits a
    /// request for <paramref name="url"/> carrying <paramref name="nonce"/>, with the page URL as <c>'self'</c>.
    /// <see cref="ExpandCssImports"/> passes each <c>@import</c> here with a <see langword="null"/> nonce,
    /// because "fetch a style resource" never gives the request one. When no policy is configured every
    /// stylesheet is allowed.
    /// </summary>
    private bool IsStyleFetchAllowedByCsp(string url, string? nonce) =>
        Csp == null || Csp.AllowsExternalStyle(url, _pageUrl, nonce);

    /// <summary>
    /// Fires the <c>load</c> — or <c>error</c> — event on a <c>&lt;link rel="stylesheet"&gt;</c> whose
    /// sheet has been fetched, per HTML §4.2.4 "link type stylesheet". Nothing dispatched these at
    /// all, so a page that waits for <c>link.onload</c> before declaring itself ready never got the
    /// callback (WPT issue #1497 problem 26,
    /// <c>uievents/…/UIEvent.load.stylesheet</c>).
    /// <para>
    /// Only a link in <em>this bridge's page document</em> fetches, so a detached one stays silent until
    /// it is inserted. That test is deliberately not <see cref="DomNode.IsConnected"/>, which is true in
    /// any document: HTML fetches only once the link is <em>browsing-context connected</em>, which a
    /// link in a <c>createHTMLDocument</c> document never is, and the base URL, CSP policy and cascade
    /// the outcome is decided against below are all the page's. So a link in a frame's document fires
    /// neither event here either, although a browser fetches it against the frame's own base URL and
    /// fires one. The event fires once per <c>href</c>: re-pointing the link at a different sheet
    /// is a new fetch and fires again, while re-inserting it, or writing the same href twice, does
    /// not. Whether the fetch succeeded is decided the same way the cascade decides it — the CSP
    /// gate, then the resource loader — so the event never disagrees with whether the sheet applied.
    /// </para>
    /// </summary>
    private void FireStylesheetLinkLoad(DomElement element)
    {
        if (_realm is null || !IsExternalStylesheet(element))
            return;
        if (!ReferenceEquals(GetTreeRoot(element), _document))
            return;
        if (!TryGetAttribute(element, "href", out var href) || string.IsNullOrWhiteSpace(href))
            return;

        var state = StyleSheetStateFor(element);
        if (string.Equals(state.LoadEventFiredForHref, href, StringComparison.Ordinal))
            return;
        state.LoadEventFiredForHref = href;

        // The resource loader only takes absolute URLs, so the content attribute is resolved against
        // the page URL first — the same rebasing the renderer does for a linked sheet. Skipping it
        // made every relative href look like a failed fetch and dispatched `error` for a sheet that
        // then applied perfectly well. A data: href is already its own absolute URL and carries the
        // sheet in it, so it is read through the same seam the cascade uses rather than rebased and
        // handed to the loader, which knows no data: scheme and reported the sheet as failed.
        var loaded = IsExternalStyleAllowedByCsp(element, href) &&
                     !string.IsNullOrEmpty(FetchStyleSheetText(ResolveStyleSheetLinkUrl(href)));

        try
        {
            // The event object is built through the realm (JSEAL); the two members keep the
            // enumerable/configurable data-property attributes they had, which is what
            // JsPropertyFlags.Default spells. The dispatcher takes that handle as it is.
            var evt = Realm.NewObject();
            Realm.DefineValue(evt, "type", JsValue.String(loaded ? "load" : "error"));
            Realm.DefineValue(evt, "bubbles", JsValue.False);
            _eventDispatch.DispatchEventOnElement(element, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.FireStylesheetLinkLoad",
                $"stylesheet load handler error for '{href}': {ex.Message}", ex);
        }
    }

    /// <summary>Fires the stylesheet <c>load</c> event for <paramref name="element"/> and every
    /// <c>&lt;link rel="stylesheet"&gt;</c> beneath it — the subtree counterpart used when a whole
    /// fragment is inserted or the document finishes loading.</summary>
    private void FireDescendantStylesheetLinkLoads(DomElement element)
    {
        FireStylesheetLinkLoad(element);
        // Snapshot before iterating: a load handler can structurally mutate the tree mid-walk, the
        // same hazard FireDescendantOnloads documents.
        foreach (var child in SnapshotChildren(element))
        {
            if (child is DomElement childElement)
                FireDescendantStylesheetLinkLoads(childElement);
        }
    }

    /// <summary>
    /// Asks the loader to start fetching every external stylesheet in <paramref name="styleElements"/>
    /// that this document has not already fetched. Multithreading roadmap item #2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The URL handed over is the same string <see cref="GetStyleElementSourceText"/> will pass to
    /// <see cref="FetchExternalStylesheet"/> — the href resolved through
    /// <see cref="ResolveStyleSheetLinkUrl"/> — because a prefetch keyed on a differently-normalized
    /// URL would simply never be consumed, silently doubling the requests instead of overlapping
    /// them. The two must be changed together; they were the raw <c>href</c> on both sides until the
    /// consuming path started resolving it.
    /// </para>
    /// <para>
    /// The CSP check is applied here too: a sheet the policy blocks must not have a request put on
    /// the wire on its behalf, and the consuming path already refuses it.
    /// </para>
    /// </remarks>
    private void PrefetchExternalStylesheets(List<DomElement> styleElements)
    {
        List<string>? urls = null;

        foreach (var styleEl in styleElements)
        {
            if (!string.Equals(styleEl.TagName, "link", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!TryGetAttribute(styleEl, "href", out var href) || string.IsNullOrEmpty(href))
                continue;

            // A data: sheet is decoded in-process by the consuming path and never reaches the
            // loader, so prefetching one buys nothing and would leave an unconsumed entry behind.
            if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Already fetched for this document — the consuming path reads the cached text and
            // never calls the loader, so a request here would be pure waste.
            if (StyleSheetStateFor(styleEl).FetchedCss.TryGet(out _))
                continue;

            if (!IsExternalStyleAllowedByCsp(styleEl, href))
                continue;

            (urls ??= []).Add(ResolveStyleSheetLinkUrl(href));
        }

        if (urls is not null)
            _resources.Prefetch(urls);
    }

    /// <summary>
    /// Fetches an external CSS stylesheet from an HTTP/HTTPS URL.
    /// Returns the CSS text content, or <c>null</c> on failure.
    /// </summary>
    private string? FetchExternalStylesheet(string url)
    {
        try
        {
            // The file/http dispatch policy lives in the loader, not here.
            return _resources.LoadText(url);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.HtmlRenderer, "DomBridge.FetchExternalStylesheet", $"Failed to fetch stylesheet '{url}': {ex.Message}", ex);
            return null;
        }
    }
}
