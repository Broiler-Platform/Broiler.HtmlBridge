using Broiler.Dom;
using Broiler.HtmlBridge.Scripting;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The Attach-time Content Security Policy gate for styles: <c>style-src-attr</c> and
/// <c>style-src-elem</c> (each falling back to <c>style-src</c> and <c>default-src</c>) enforced on the
/// parsed document, from a <c>&lt;meta&gt;</c>-delivered policy's meta onward. The same walk records the
/// blocks it found before that meta for the <c>@import</c> gate in StyleSheets.Imports.cs, which runs
/// much later, on a render projection of a tree page script may have changed.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Enforces the Content Security Policy <c>style-src</c> family on the parsed
    /// DOM so blocked inline styles do not render: an inline <c>style="…"</c>
    /// attribute blocked by <c>style-src-attr</c> (→ <c>style-src</c> →
    /// <c>default-src</c>) is stripped, and a <c>&lt;style&gt;</c> element blocked
    /// by <c>style-src-elem</c> (same fallback chain) is removed. Only the style
    /// directives are consulted — script/event-handler enforcement is intentionally
    /// left to the script pipeline — so this is safe to call on any parsed document.
    /// </summary>
    public void ApplyStyleContentSecurityPolicy(ContentSecurityPolicy? csp)
    {
        // Rebuilt by every run, so it always describes the walk that last decided which blocks stay.
        _importsBeforePolicyMeta = null;
        if (csp == null || DocumentElement == null)
            return;

        // CSP §"Processing a `meta` element": a policy delivered by
        // <meta http-equiv="Content-Security-Policy"> is enforced from the point the parser reaches
        // the meta — markup already parsed is *not* retroactively blocked. Enforcing document-wide
        // stripped a style attribute that precedes the meta, including on the ancestors that
        // *contain* it: WPT content-security-policy/style-src/inline-style-attribute-on-html has
        // <html style="background-color: blue"> before a `style-src 'none'` meta, and rendered white
        // instead of blue.
        //
        // A pre-order walk visits an element's start tag in parse order, and visits ancestors before
        // descendants, so "not yet reached the meta" is exactly "this start tag was parsed first".
        // A policy with no meta in the document came from a header and applies document-wide.
        var policyMeta = FindCspMetaElement(DocumentElement);
        ApplyStyleCsp(
            DocumentElement, csp,
            blockStyleAttribute: !csp.AllowsInlineStyleAttribute(),
            policyMeta,
            enforcing: policyMeta == null);
    }

    /// <summary>
    /// The <c>&lt;meta http-equiv="Content-Security-Policy"&gt;</c> element that delivered the
    /// document's policy, in document order, or <c>null</c> when none is present (a header-delivered
    /// policy). Mirrors the acceptance rules of <c>CspMetaDiscovery.FindPolicyContent</c>, which
    /// parses the same meta out of the source text, exactly: <c>http-equiv</c> is compared untrimmed, as
    /// HTML compares it. A trim here found a meta the discovery had ignored, so a header policy stayed in
    /// force while every block before that meta was exempted from it.
    /// </summary>
    private DomElement? FindCspMetaElement(DomElement element)
    {
        if (!IsText(element) &&
            element.TagName.Equals("meta", StringComparison.OrdinalIgnoreCase) &&
            TryGetAttribute(element, "http-equiv", out var httpEquiv) &&
            string.Equals(httpEquiv, "Content-Security-Policy", StringComparison.OrdinalIgnoreCase) &&
            TryGetAttribute(element, "content", out var content) &&
            !string.IsNullOrWhiteSpace(content))
        {
            return element;
        }

        foreach (var child in ChildElements(element))
        {
            var found = FindCspMetaElement(child);
            if (found != null)
                return found;
        }

        return null;
    }

    /// <summary>
    /// Walks the document in parse order applying the style-src family. <paramref name="enforcing"/>
    /// starts <c>false</c> for a meta-delivered policy and flips to <c>true</c> at
    /// <paramref name="policyMeta"/>; it is threaded through the walk (rather than being recomputed
    /// per element) so the flip is observed by every subsequent element in document order. Each element
    /// reached before the flip goes to <see cref="RecordImportsBeforePolicyMeta"/>, so the
    /// <c>@import</c> gate exempts the imports of exactly the blocks this walk exempted.
    /// </summary>
    private bool ApplyStyleCsp(
        DomElement element, ContentSecurityPolicy csp, bool blockStyleAttribute,
        DomElement? policyMeta, bool enforcing)
    {
        if (ReferenceEquals(element, policyMeta))
            enforcing = true;

        if (enforcing && !IsText(element))
        {
            if (element.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                var nonce = TryGetAttribute(element, "nonce", out var n) ? n : null;
                if (!csp.AllowsInlineStyleElement(nonce, GetStyleElementCssText(element)))
                {
                    element.Remove();
                    return enforcing;
                }
            }

            if (blockStyleAttribute && HasAttr(element, "style"))
            {
                RemoveAttr(element, "style");
                InlineStyle(element).Clear();
                InvalidateStyleScope(element);
            }
        }
        else if (!enforcing)
        {
            RecordImportsBeforePolicyMeta(element);
        }

        // Snapshot: a blocked <style> child removes itself from this collection.
        foreach (var child in ChildElements(element).ToArray())
            enforcing = ApplyStyleCsp(child, csp, blockStyleAttribute, policyMeta, enforcing);

        return enforcing;
    }
}
