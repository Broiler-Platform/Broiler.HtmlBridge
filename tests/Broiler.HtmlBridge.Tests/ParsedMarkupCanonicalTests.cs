using System.Net;
using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// What a document, a frame and a written fragment hold once markup has been parsed — the doctype, the
/// render mode a scripted frame is stamped with, and when a parsed custom element is upgraded — asserted
/// from page script, written before the bridge's own markup readers were swapped for the shared
/// <c>Broiler.Dom.Html</c> parser and tokenizer.
/// <para>
/// <b>What was being replaced.</b> The bridge parsed a frame with <c>HtmlDocumentParser</c> and then threw
/// the parsed doctype away, re-reading it from the source string with a regular expression; the same regex
/// served a sub-document's <c>document.write</c>. That regex matched <c>&lt;!DOCTYPE</c> anywhere, a
/// comment included, and could not read a <c>SYSTEM</c>-only or single-quoted identifier. A scripted
/// <c>src</c> frame's render mode came from a third reader, a hand scanner that skipped any Unicode
/// whitespace and took any name. And every fragment the bridge parsed (<c>innerHTML</c>, <c>outerHTML</c>,
/// <c>insertAdjacentHTML</c>, <c>document.write</c>) was first moved into a staging fragment owned by the
/// page's document, which is a document whose mutations the custom-element registry hears.
/// </para>
/// <para>
/// <b>Two kinds of test live here, and the name says which.</b> Most assert what the HTML Standard and
/// Chromium answer; the ones that failed against the old readers say what changed. The rest are named
/// <c>Characterization_</c> and pin what the bridge delivers where it is known to differ from Chromium, so
/// a change to it is a decision rather than a side effect; each says what Chromium does instead. The
/// skipped tests spell the browser's answer and name the package defect that keeps it out of reach.
/// </para>
/// <para>
/// The custom-element reaction order and the adoption of parsed nodes are in
/// <c>ParsedMarkupCanonicalTests.Reactions.cs</c>.
/// </para>
/// </summary>
public partial class ParsedMarkupCanonicalTests
{
    private const string PageUrl = "https://example.test/parsed-markup";

    /// <summary>
    /// A page with <c>#host</c> and <c>#swap</c> to write markup into and a frame in front of <c>#out</c>.
    /// The frame is whatever <paramref name="frameAttributes"/> make it: a <c>srcdoc</c> (its inner
    /// attributes single-quoted so the double-quoted value survives) or a <c>data:</c> <c>src</c>, so no
    /// fixture needs the network.
    /// </summary>
    private static string FramePage(string frameAttributes) =>
        "<html><body><div id=\"host\"></div><div id=\"swap\"><span id=\"old\"></span></div>" +
        $"<iframe id=\"f\" {frameAttributes}></iframe>" +
        "<div id=\"out\"></div></body></html>";

    /// <summary>A <c>srcdoc</c> frame whose body is one paragraph, <c>#fp</c>.</summary>
    private static readonly string PlainFramePage =
        FramePage("srcdoc=\"<html><body><p id='fp'>x</p></body></html>\"");

    /// <summary>
    /// Runs <paramref name="script"/> against <paramref name="pageHtml"/>, writes its value to <c>#out</c>
    /// and returns the whole serialization. Reading the answer out of the serialized DOM keeps the test to
    /// the engine's public surface, as the neighbouring page-script suites do.
    /// </summary>
    private static string RunForHtml(string pageHtml, string script)
    {
        var html = new ScriptEngine().Execute(
            [$"document.getElementById('out').textContent = String({script});"],
            pageHtml,
            PageUrl);

        Assert.NotNull(html);
        return html!;
    }

    /// <summary>As <see cref="RunForHtml"/>, returning only what the script wrote to <c>#out</c>.</summary>
    private static string Run(string pageHtml, string script)
    {
        var html = RunForHtml(pageHtml, script);

        const string open = "<div id=\"out\">";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return html[start..end];
    }

    /// <summary>
    /// Loads a <c>data:</c> frame holding <paramref name="frameMarkup"/>, changes its <c>#x</c> so the live
    /// document diverges from the resource, and returns the document stamped on the frame for the renderer
    /// (<c>data-broiler-frame-document</c>, decoded). Its prefix is the frame's render mode:
    /// <c>&lt;!DOCTYPE html&gt;</c> for standards, nothing for quirks.
    /// </summary>
    private static string StampedFrameDocument(string frameMarkup)
    {
        var html = RunForHtml(
            FramePage($"src=\"data:text/html,{frameMarkup}\""),
            "(function () { document.getElementById('f').contentDocument.getElementById('x').textContent = 'b'; return 'ok'; })()");

        const string open = "data-broiler-frame-document=\"";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the scripted frame was not stamped: {html}");
        start += open.Length;
        var end = html.IndexOf('"', start);
        Assert.True(end >= 0, $"unterminated frame stamp: {html}");
        return WebUtility.HtmlDecode(html[start..end]);
    }

    private const string FrameDoctypeProbe = """
        (function () {
          var d = document.getElementById('f').contentDocument;
          return (d.doctype ? d.doctype.name + '|' + d.doctype.publicId + '|' + d.doctype.systemId : 'null') +
                 ' first=' + d.firstChild.nodeType;
        })()
        """;

    // ---------------------------------------------------------------------
    //  A frame's doctype is the parser's
    // ---------------------------------------------------------------------

    [Fact]
    public void AFramesSystemOnlyDoctypeIsTheParsersNode()
    {
        // CHANGED. The regex that re-read a frame's doctype wanted PUBLIC or '>' straight after the name,
        // so the about:legacy-compat form — the one HTML itself recommends for XSLT output — gave the frame
        // no doctype at all and <html> became its first child. The parser's doctype is used now, and it
        // carries the SYSTEM identifier (DOM §4.5: a doctype is the document's first child).
        Assert.Equal(
            "html||about:legacy-compat first=10",
            Run(FramePage("srcdoc=\"<!DOCTYPE html SYSTEM 'about:legacy-compat'><html><body><p id='fp'>x</p></body></html>\""),
                FrameDoctypeProbe));
    }

    [Fact]
    public void AFramesSingleQuotedPublicDoctypeIsRead()
    {
        // CHANGED. HTML §13.2.5.58 accepts either quote around an identifier; the regex accepted only the
        // double one, so this frame had no doctype.
        Assert.Equal(
            "html|-//W3C//DTD HTML 4.01//EN|http://www.w3.org/TR/html4/strict.dtd first=10",
            Run(FramePage("srcdoc=\"<!DOCTYPE html PUBLIC '-//W3C//DTD HTML 4.01//EN' 'http://www.w3.org/TR/html4/strict.dtd'><html><body><p id='fp'>x</p></body></html>\""),
                FrameDoctypeProbe));
    }

    [Fact]
    public void AFramesDoubleQuotedPublicDoctypeReadsAsBefore()
    {
        // The form both readers agreed on, so the change keeps it.
        Assert.Equal(
            "html|-//W3C//DTD XHTML 1.0 Strict//EN|http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd first=10",
            Run(FramePage("srcdoc=\"<!DOCTYPE HTML PUBLIC &quot;-//W3C//DTD XHTML 1.0 Strict//EN&quot; &quot;http://www.w3.org/TR/xhtml1/DTD/xhtml1-strict.dtd&quot;><html><body><p id='fp'>x</p></body></html>\""),
                FrameDoctypeProbe));
    }

    [Fact]
    public void ADoctypeInsideAFramesCommentIsNotItsDoctype()
    {
        // CHANGED. The regex was not anchored and read no markup, so a doctype quoted in a comment became
        // the frame's doctype. A comment is a comment token (HTML §13.2.5.43); nothing inside it is one.
        // Deliberately silent on firstChild: this parser puts the comment in <head>, where Chromium puts it
        // on the Document, and that is a separate parser gap.
        Assert.Equal(
            "doctype=null",
            Run(FramePage("srcdoc=\"<!-- <!DOCTYPE html> --><html><body><p id='fp'>x</p></body></html>\""),
                "(function () { var d = document.getElementById('f').contentDocument; " +
                "return 'doctype=' + (d.doctype === null ? 'null' : d.doctype.name); })()"));
    }

    [Fact]
    public void SubDocumentWriteDoesNotTakeADoctypeFromAComment()
    {
        // CHANGED, for the same reason as the frame above: document.write into a frame's reopened
        // document read its doctype with the same regex.
        Assert.Equal(
            "doctype=null w=found",
            Run(PlainFramePage, """
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  d.open();
                  d.write("<!-- <!DOCTYPE html> --><p id='w'>w</p>");
                  return 'doctype=' + (d.doctype === null ? 'null' : d.doctype.name) +
                         ' w=' + (d.getElementById('w') ? 'found' : 'missing');
                })()
                """));
    }

    [Fact]
    public void SubDocumentWriteReadsASystemOnlyDoctype()
    {
        // CHANGED: the write path's regex could not read the SYSTEM-only form either.
        Assert.Equal(
            "doctype=html|about:legacy-compat w=found",
            Run(PlainFramePage, """
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  d.open();
                  d.write("<!DOCTYPE html SYSTEM 'about:legacy-compat'><p id='w'>w</p>");
                  return 'doctype=' + (d.doctype ? d.doctype.name + '|' + d.doctype.systemId : 'null') +
                         ' w=' + (d.getElementById('w') ? 'found' : 'missing');
                })()
                """));
    }

    [Fact]
    public void TheMainDocumentKeepsItsParsedDoctypeAndTitle()
    {
        // The page's own parse already used the parser's doctype; this guards the rewrite of the call that
        // hands it over. The title is the parser's, trimmed, and a doctype named html still serializes as
        // the standards-mode doctype the renderer re-reads.
        var html = RunForHtml(
            "<!DOCTYPE html SYSTEM \"about:legacy-compat\"><html><head><title> t </title></head>" +
            "<body><div id=\"out\"></div></body></html>",
            "document.doctype.name + '|' + document.doctype.systemId + ' first=' + " +
            "(document.firstChild === document.doctype) + ' title=' + document.title");

        Assert.Contains("<div id=\"out\">html|about:legacy-compat first=true title=t</div>", html);
        Assert.StartsWith("<!DOCTYPE html>", html);
    }

    [Fact]
    public void ALateDoctypeIsNotTheDocumentsDoctype()
    {
        // HTML §13.2.6.4.7: a DOCTYPE token in "in body" is a parse error and is ignored, so a page that
        // opens with content has no doctype however late one follows. Chromium: document.doctype === null.
        Assert.Equal(
            "null",
            Run("<p>x</p><!DOCTYPE html><div id=\"out\"></div>", "String(document.doctype)"));
    }

    // ---------------------------------------------------------------------
    //  A scripted src frame's render mode
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData("<!DOCTYPE html><p id='x'>a</p>")]
    [InlineData("<!-- lead --><!DOCTYPE html><p id='x'>a</p>")]
    [InlineData(" \n\t<!DOCTYPE html><p id='x'>a</p>")]
    [InlineData("<!doctype HTML><p id='x'>a</p>")]
    public void AScriptedFramesLeadingDoctypeKeepsStandardsMode(string frameMarkup)
    {
        // HTML §13.2.6.4.1: in the "initial" insertion mode comments and ASCII whitespace are allowed
        // before the DOCTYPE, and a DOCTYPE named html selects no-quirks mode. Unchanged by the reader swap.
        Assert.StartsWith("<!DOCTYPE html>", StampedFrameDocument(frameMarkup));
    }

    [Theory]
    [InlineData("<p id='x'>a</p>")]
    [InlineData("x<!DOCTYPE html><p id='x'>a</p>")]
    [InlineData("<!-- <!DOCTYPE html> --><p id='x'>a</p>")]
    public void AScriptedFramesMissingOrLateDoctypeIsQuirks(string frameMarkup)
    {
        // Any other first token leaves "initial" with quirks mode set, and a DOCTYPE after it is a parse
        // error that changes nothing (§13.2.6.4.7) — even though this parser still builds a doctype node
        // for the late one. Unchanged by the reader swap.
        Assert.StartsWith("<html", StampedFrameDocument(frameMarkup));
    }

    [Fact]
    public void AnXmlDeclarationBeforeAFramesDoctypeKeepsStandardsMode()
    {
        // CHANGED. `<?` opens a bogus comment (HTML §13.2.5.6, unexpected-question-mark-instead-of-tag-name),
        // so a prolog is a comment as far as the mode is concerned and the DOCTYPE after it still counts.
        // The hand scanner stopped at anything that was not `<!--` and stamped quirks.
        Assert.StartsWith("<!DOCTYPE html>", StampedFrameDocument("<?xml version='1.0'?><!DOCTYPE html><p id='x'>a</p>"));
    }

    [Fact]
    public void ANonBreakingSpaceBeforeAFramesDoctypeMakesItQuirks()
    {
        // CHANGED. Only ASCII whitespace is ignored in "initial"; U+00A0 is a character token that ends it
        // in quirks mode. The hand scanner skipped anything char.IsWhiteSpace accepts, which includes it.
        Assert.StartsWith("<html", StampedFrameDocument("&#160;<!DOCTYPE html><p id='x'>a</p>"));
    }

    [Fact]
    public void AFramesDoctypeNotNamedHtmlIsQuirks()
    {
        // CHANGED. A DOCTYPE whose name is not html sets quirks mode (§13.2.6.4.1), which is the rule the
        // page's own serialization already applied (DomBridge/Serialization.cs, SelectsStandardsMode). The
        // hand scanner only looked for the `<!doctype` prefix.
        Assert.StartsWith("<html", StampedFrameDocument("<!DOCTYPE foo><p id='x'>a</p>"));
    }

    [Theory]
    [InlineData("<!---><!DOCTYPE html><!-- --><p id='x'>a</p>")]
    [InlineData("<!-- lead --><!---><!DOCTYPE html><!-- --><p id='x'>a</p>")]
    public void AnAbruptlyClosedCommentBeforeAFramesDoctypeKeepsStandardsMode(string frameMarkup)
    {
        // HTML §13.2.5.44/45: `<!--->` is an abrupt-closing-of-empty-comment and closes the comment, so the
        // DOCTYPE after it is still the first token that counts. Handled natively by HtmlTokenizer
        // (Broiler.Dom.Html 0.1.0-preview.3).
        Assert.StartsWith("<!DOCTYPE html>", StampedFrameDocument(frameMarkup));
    }
}
