using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The two public readers that pull one value out of raw HTML source before any DOM exists —
/// <see cref="HtmlBaseHref.TryFindBaseHref"/> (the WPT runner's stylesheet inliner) and
/// <see cref="ContentSecurityPolicy.ExtractNonceFromAttributes"/> (the CLI's script extraction) — written
/// before their regular expressions were swapped for the shared <c>Broiler.Dom.Html</c> tokenizer.
/// <para>
/// <b>What the regexes got wrong.</b> Neither read markup. <c>&lt;base\b[^&gt;]*&gt;</c> found a base in a
/// comment, in script or style text, in template contents and inside another tag's attribute value; it was
/// cut short by a quoted <c>&gt;</c>; <c>\bhref</c> matched the end of <c>data-href</c>; <c>\b</c> accepted
/// <c>&lt;base-x&gt;</c>; and nothing decoded a character reference. The nonce reader had the same
/// suffix and decoding faults and kept a closing quote when an earlier attribute's value spelled
/// <c>nonce=</c>. Each test that failed against them says what changed; the rest pin forms both readers
/// agree on, several of them copied from the consumer's own suite
/// (<c>Broiler.Wpt.Tests/StylesheetBaseHrefInliningTests.cs</c>).
/// </para>
/// <para>
/// <b>What the tokenizer is measured against.</b> For the base href, the DOM the bridge builds from the
/// same markup: <c>DomBridge.TryFindDocumentBaseHref</c> walks it for the first <c>&lt;base&gt;</c> with a
/// non-whitespace <c>href</c>, and <see cref="HtmlBaseHref"/> exists so the inliner and that transform
/// agree. Where the regexes already had the HTML Standard's answer and the tokenizer loses it — whitespace
/// before an attribute's <c>=</c>, and for the nonce a list cut off inside a later quoted value — the
/// readers repair the input and keep that answer, and the tests say so. The skipped tests spell the
/// Standard's answer where the consumed tokenizer cannot reach it.
/// </para>
/// </summary>
public class HtmlSourceReaderTests
{
    // ---------------------------------------------------------------------
    //  HtmlBaseHref.TryFindBaseHref
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("""<base href="resources/">""", "resources/")]
    [InlineData("""<base href='resources/'>""", "resources/")]
    [InlineData("<base href=resources/>", "resources/")]
    [InlineData("""<BASE HREF="  resources/  ">""", "resources/")]
    // The FIRST base with a non-whitespace href wins. That is the rule of the bridge's DOM walk
    // (DomBridge.TryFindDocumentBaseHref), not HTML §4.2.3, which takes the first base with any href.
    [InlineData("""<base><base href="a/"><base href="b/">""", "a/")]
    [InlineData("""<base target="_blank" href="a/">""", "a/")]
    public void TryFindBaseHref_KeepsThePinnedForms(string html, string expected)
    {
        Assert.True(HtmlBaseHref.TryFindBaseHref(html, out var baseHref));
        Assert.Equal(expected, baseHref);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html><head></head></html>")]
    [InlineData("<base>")]
    // A deliberate departure, shared with the DOM walk: HTML §4.2.3 makes a base with a blank href the one
    // that sets the document base URL (which is then the document's own URL), and stops looking there. The
    // bridge reports no base instead, so a later base would win.
    [InlineData("""<base href="">""")]
    [InlineData("""<base href="   ">""")]
    [InlineData("""<basefont href="x/">""")]
    public void TryFindBaseHref_ReportsNoBase(string html)
    {
        Assert.False(HtmlBaseHref.TryFindBaseHref(html, out var baseHref));
        Assert.Equal(string.Empty, baseHref);
    }

    [Theory]
    [InlineData("""<!-- <base href="wrong/"> --><base href="right/">""")]
    [InlineData("""<script>var s = '<base href="wrong/">';</script><base href="right/">""")]
    [InlineData("""<style>/* <base href="wrong/"> */</style><base href="right/">""")]
    [InlineData("""<noscript><base href="wrong/"></noscript><base href="right/">""")]
    [InlineData("""<template><template></template><base href="wrong/"></template><base href="right/">""")]
    [InlineData("""<a title='<base href="wrong/">'></a><base href="right/">""")]
    public void TryFindBaseHref_OnlyCountsABaseElementInTheDocument(string html)
    {
        // CHANGED: every one of these found "wrong/". A comment, the raw text of script, style and noscript
        // (scripting is enabled), a template's contents (HTML §4.12.3: not in the document) and another
        // tag's attribute value hold no base element, and the DOM built from the same markup has none there.
        // The nested template checks that an inner end tag does not re-open the outer contents.
        Assert.True(HtmlBaseHref.TryFindBaseHref(html, out var baseHref));
        Assert.Equal("right/", baseHref);
    }

    [Fact]
    public void TryFindBaseHref_ASelfClosingTemplateOpensNoContents()
    {
        // Pins the self-closing branch of the template depth counter: counting `<template/>` as open would
        // hide every later base. The regex found this base too. It mirrors HtmlDocumentParser, which opens no
        // element for a self-closing tag. It is not Chromium, which ignores the slash on a non-void element,
        // keeps the template open and puts this base in its contents, so that document has no base.
        Assert.True(HtmlBaseHref.TryFindBaseHref("""<template/><base href="right/">""", out var baseHref));
        Assert.Equal("right/", baseHref);
    }

    [Fact(Skip = "HtmlTokenizer (Broiler.Dom.Html 0.1.0-preview.2) reads only script, style and noscript as raw " +
                 "text, so a <base> in iframe, xmp, noembed or noframes text (or in title or textarea RCDATA) is a " +
                 "start tag, here as in the DOM built from it. The regex this replaced found \"wrong/\" too.")]
    public void TryFindBaseHref_IgnoresABaseInsideAnIframesText()
    {
        // HTML §13.2.6.4.7 switches the tokenizer to RAWTEXT for an iframe's content, so it holds no element.
        Assert.True(HtmlBaseHref.TryFindBaseHref("""<iframe><base href="wrong/"></iframe><base href="right/">""", out var baseHref));
        Assert.Equal("right/", baseHref);
    }

    [Fact]
    public void TryFindBaseHref_ABaseOnlyInACommentIsNoBase()
    {
        // CHANGED: this answered true with "wrong/".
        Assert.False(HtmlBaseHref.TryFindBaseHref("""<!-- <base href="wrong/"> -->""", out var baseHref));
        Assert.Equal(string.Empty, baseHref);
    }

    [Theory]
    // CHANGED: a character reference is decoded, as getAttribute('href') returns it (HTML §13.2.5.38-40).
    [InlineData("""<base href="a&amp;b/">""", "a&b/")]
    // CHANGED: `\bhref` matched the end of data-href and returned "wrong/"; the name is matched whole.
    [InlineData("""<base data-href="wrong/" href="right/">""", "right/")]
    // CHANGED: the regex's tag ended at the quoted '>' and found no href at all.
    [InlineData("""<base title="a>b" href="right/">""", "right/")]
    // CHANGED: an unquoted value runs to whitespace or '>' and keeps a quote (§13.2.5.38); the regex
    // stopped at the quote and returned "a".
    [InlineData("<base href=a\"b>", "a\"b")]
    public void TryFindBaseHref_ReadsAttributesAsTheTokenizerDoes(string html, string expected)
    {
        Assert.True(HtmlBaseHref.TryFindBaseHref(html, out var baseHref));
        Assert.Equal(expected, baseHref);
    }

    [Fact]
    public void TryFindBaseHref_ABaseDashXElementIsNotABase()
    {
        // CHANGED: `<base\b` matched the start of base-x, whose tag name is "base-x".
        Assert.False(HtmlBaseHref.TryFindBaseHref("""<base-x href="x/">""", out _));
    }

    [Theory]
    [InlineData("""<base href ="x/">""")]
    [InlineData("""<base href = "x/">""")]
    [InlineData("<base target=_top href\n=\nx/>")]
    [InlineData("""<base href=""><base HREF = "x/">""")]
    public void TryFindBaseHref_ToleratesWhitespaceAroundEquals(string html)
    {
        // HTML §13.2.5.34 (after attribute name state): whitespace before '=' is skipped and the value that
        // follows belongs to the attribute. Unchanged by the swap: the regex read "x/", and so does this.
        // HtmlTokenizer (Broiler.Dom.Html 0.1.0-preview.2, BeforeAttributeName) would commit href with an
        // empty value there, as the DOM built from this markup still does, so the reader closes the
        // whitespace up before tokenizing (HtmlSourceAttributes.CloseSpaceBeforeEquals).
        Assert.True(HtmlBaseHref.TryFindBaseHref(html, out var baseHref));
        Assert.Equal("x/", baseHref);
    }

    [Theory]
    [InlineData("""<base title="href =wrong/" href="right/">""")]
    [InlineData("""<!-- <base href = "wrong/"> --><base data-href = "wrong/" href="right/">""")]
    public void TryFindBaseHref_ClosesOnlyTheSpaceAfterAnHrefAttributeName(string html)
    {
        // Guards that repair: `href =` spelled inside another attribute's value or a comment, and a spaced
        // name that only ends in href, are not the href attribute, and the reader still finds the real one.
        Assert.True(HtmlBaseHref.TryFindBaseHref(html, out var baseHref));
        Assert.Equal("right/", baseHref);
    }

    // ---------------------------------------------------------------------
    //  ContentSecurityPolicy.ExtractNonceFromAttributes
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(" nonce=\"abc\" src=\"a.js\"", "abc")]
    [InlineData(" nonce='abc'", "abc")]
    [InlineData(" nonce=abc", "abc")]
    [InlineData(" NONCE=\"abc\"", "abc")]
    [InlineData("nonce=\"abc\"", "abc")]
    [InlineData(" nonce=\"first\" nonce=\"second\"", "first")]
    [InlineData(" nonce=\"\"", "")]
    [InlineData(" src=\"a.js\"", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void ExtractNonce_KeepsTheQuotedUnquotedAndCaseForms(string attributes, string? expected)
    {
        Assert.Equal(expected, ContentSecurityPolicy.ExtractNonceFromAttributes(attributes));
    }

    [Theory]
    // CHANGED: a character reference is decoded, as the nonce IDL attribute would return it.
    [InlineData(" nonce=\"a&amp;b\"", "a&b")]
    // CHANGED: `\bnonce` matched the end of data-nonce and returned "wrong".
    [InlineData(" data-nonce=\"wrong\" nonce=\"right\"", "right")]
    // CHANGED: the unquoted branch matched inside the title's value and returned `wrong"`.
    [InlineData(" title=\"nonce=wrong\" nonce=\"right\"", "right")]
    // CHANGED from null: a valueless attribute is present with the empty string (HTML §13.2.5.33), and
    // every ContentSecurityPolicy check treats an empty nonce as no nonce.
    [InlineData(" nonce", "")]
    // CHANGED from `"abc` (which matched no policy either): the list was cut off inside the nonce's own
    // value — the CLI's `<script([^>]*)>` capture stops at the '>' in nonce="abc>def" — so what arrived is a
    // prefix of the page's nonce. A prefix is not the nonce, and matching it against a policy would allow a
    // script a browser blocks.
    [InlineData(" nonce=\"abc", null)]
    [InlineData(" nonce='abc", null)]
    [InlineData(" src=\"a.js\" nonce=\"abc", null)]
    public void ExtractNonce_ReadsTheNonceAttributeItself(string attributes, string? expected)
    {
        Assert.Equal(expected, ContentSecurityPolicy.ExtractNonceFromAttributes(attributes));
    }

    [Theory]
    [InlineData(" nonce=\"abc\" src=\"a.js\" data-x=\"a")]
    [InlineData(" nonce=\"abc\" data-x='it\"s")]
    [InlineData(" nonce=abc title=\"x nonce='y")]
    public void ExtractNonce_ReadsANonceSpelledBeforeACutOffValue(string attributes)
    {
        // Unchanged by the swap. The CLI's capture stops at the first '>', even one inside a quoted value, so
        // <script nonce="abc" src="a.js" data-x="a>b"> arrives cut off inside data-x. The regex read the nonce
        // spelled before the cut, as a browser reads it from the whole tag. The tokenizer finds no tag in a
        // cut-off list, so the reader closes the open quote and reads the list again.
        Assert.Equal("abc", ContentSecurityPolicy.ExtractNonceFromAttributes(attributes));
    }

    [Theory]
    [InlineData(" nonce =\"abc\"")]
    [InlineData(" nonce = abc")]
    [InlineData("nonce\t=\t'abc' src=\"a.js\"")]
    public void ExtractNonce_ToleratesWhitespaceAroundEquals(string attributes)
    {
        // HTML §13.2.5.34: whitespace before '=' is skipped and the value belongs to the attribute. Unchanged
        // by the swap: the regex read "abc", and so does this, because the reader closes the whitespace up
        // before tokenizing. HtmlTokenizer (Broiler.Dom.Html 0.1.0-preview.2, BeforeAttributeName) would
        // otherwise commit nonce with an empty value, which every policy check treats as no nonce.
        Assert.Equal("abc", ContentSecurityPolicy.ExtractNonceFromAttributes(attributes));
    }
}
