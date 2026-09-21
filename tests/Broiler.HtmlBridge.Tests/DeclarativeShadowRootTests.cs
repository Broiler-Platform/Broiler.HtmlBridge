using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>&lt;template shadowrootmode&gt;</c> in parsed markup — the declarative counterpart of
/// <c>attachShadow()</c> (HTML §13.2.6.4.4), asserted from page script.
/// <para>
/// The bridge used to attach these itself, in a pass over the finished document that read
/// <c>shadowrootmode</c> and nothing else. The shared tree builder does it now, as the template's end
/// tag is seen, which is where the Standard puts it; this component's part is the one answer the
/// markup does not carry — whether the entry point allows declarative shadow roots at all.
/// </para>
/// <para>
/// That answer is per entry point and not per document, which is the distinction the last case pins:
/// navigation sets the flag, <c>innerHTML</c> deliberately does not, and the whole reason the other
/// spelling is named <c>setHTMLUnsafe</c> is that turning it on is a decision about markup
/// provenance.
/// </para>
/// </summary>
public class DeclarativeShadowRootTests
{
    private const string PageUrl = "https://example.test/declarative-shadow";

    /// <summary>
    /// A page whose body is <paramref name="body"/>, with the <c>#out</c> probe div after it.
    /// </summary>
    private static string Page(string body) =>
        $"<html><body>{body}<div id=\"out\"></div></body></html>";

    private static string Run(string body, string script) =>
        PageProbe.RunAgainst(Page(body), PageUrl, script);

    [Fact]
    public void AnOpenTemplateBecomesAShadowRootAndLeavesTheLightChildrenAlone()
    {
        // The template is consumed: it is not among the host's children, and what was inside it is the
        // shadow tree. The sibling <p> is light DOM and stays exactly where it was.
        Assert.Equal(
            "root=open shadow=SPAN|s light=1|P mode=open focus=false",
            Run(
                "<div id=\"h\"><template shadowrootmode=\"open\"><span>s</span></template><p>light</p></div>",
                """
                (function () {
                  var h = document.getElementById('h');
                  var r = h.shadowRoot;
                  return 'root=' + (r ? r.mode : 'null') +
                         ' shadow=' + r.firstChild.nodeName + '|' + r.firstChild.textContent +
                         ' light=' + h.childNodes.length + '|' + h.firstChild.nodeName +
                         ' mode=' + r.mode + ' focus=' + r.delegatesFocus;
                })()
                """));
    }

    [Fact]
    public void TheOtherShadowrootAttributesAreReadToo()
    {
        // The pass this replaces read `shadowrootmode` and ignored the other three, so a declarative
        // root could never delegate focus however the markup was written. `shadowrootclonable` and
        // `shadowrootserializable` are read by the tree builder as well; neither has an IDL attribute
        // on this component's ShadowRoot wrapper, so focus delegation is the observable one here.
        Assert.Equal(
            "focus=true slot=named consumed=0",
            Run(
                "<div id=\"h\"><template shadowrootmode=\"open\" shadowrootdelegatesfocus " +
                "shadowrootclonable shadowrootserializable><span>s</span></template></div>",
                """
                (function () {
                  var r = document.getElementById('h').shadowRoot;
                  return 'focus=' + r.delegatesFocus + ' slot=' + r.slotAssignment +
                         ' consumed=' + document.getElementById('h').childNodes.length;
                })()
                """));
    }

    [Fact]
    public void AClosedDeclarativeRootIsAttachedButNotExposed()
    {
        // `mode=closed` attaches exactly as `open` does — the template is consumed and the host is
        // left with no children — and then hides the root from script, which is the whole difference.
        Assert.Equal(
            "shadow=null light=0 html=",
            Run(
                "<div id=\"h\"><template shadowrootmode=\"closed\"><span>s</span></template></div>",
                """
                (function () {
                  var h = document.getElementById('h');
                  return 'shadow=' + (h.shadowRoot === null ? 'null' : 'exposed') +
                         ' light=' + h.childNodes.length +
                         ' html=' + h.innerHTML;
                })()
                """));
    }

    [Fact]
    public void ANestedDeclarativeTemplateAttachesInsideTheShadowTree()
    {
        // A shadow tree may declare shadow roots of its own, and the tree builder reaches them because
        // it is building the shadow tree as it goes rather than walking a finished document.
        Assert.Equal(
            "outer=DIV inner=I|x",
            Run(
                "<div id=\"a\"><template shadowrootmode=\"open\"><div id=\"b\">" +
                "<template shadowrootmode=\"open\"><i>x</i></template></div></template></div>",
                """
                (function () {
                  var outer = document.getElementById('a').shadowRoot;
                  var inner = outer.firstChild.shadowRoot;
                  return 'outer=' + outer.firstChild.nodeName +
                         ' inner=' + inner.firstChild.nodeName + '|' + inner.firstChild.textContent;
                })()
                """));
    }

    [Fact]
    public void OnlyTheFirstDeclarativeTemplateOnAHostWins()
    {
        // HTML's "already has a shadow root" check: a second one is an ordinary inert <template>, so it
        // stays in the light DOM with its markup in its contents fragment.
        Assert.Equal(
            "shadow=1 light=1|TEMPLATE spare=2",
            Run(
                "<div id=\"h\"><template shadowrootmode=\"open\"><i>1</i></template>" +
                "<template shadowrootmode=\"open\"><i>2</i></template></div>",
                """
                (function () {
                  var h = document.getElementById('h');
                  return 'shadow=' + h.shadowRoot.firstChild.textContent +
                         ' light=' + h.childNodes.length + '|' + h.firstChild.nodeName +
                         ' spare=' + h.firstChild.content.firstChild.textContent;
                })()
                """));
    }

    [Fact]
    public void ADeclarativeShadowTreeRendersAndIsScopedToItsOwnRoot()
    {
        // Everything downstream of the attach is the machinery attachShadow() already drove — the
        // render-time flattening of the #shadow-root wrapper, and the two selector-scoping passes that
        // keep one tree's rules off the rest of the document. Both of those hang off a flag that only
        // attachShadow() used to set, so a declarative root that nothing marks renders unstyled beside
        // an imperative one that renders correctly. `p` matches only inside this tree and `:host` only
        // its host, while the `<p>` outside keeps neither stamp.
        Assert.Equal(
            "<html><head></head><body><div id=\"h\" data-broiler-shadow-host=\"0\">" +
            "<style data-broiler-shadow-scope=\"0\">p[data-broiler-shadow-scope=\"0\"]{color:red}" +
            "[data-broiler-shadow-host=\"0\"]{color:blue}</style>" +
            "<p data-broiler-shadow-scope=\"0\">s</p></div><p>outside</p>" +
            "<div id=\"out\">ok</div></body></html>",
            PageProbe.Render(
                [PageProbe.Probe("'ok'")],
                Page(
                    "<div id=\"h\"><template shadowrootmode=\"open\">" +
                    "<style>p{color:red}:host{color:blue}</style><p>s</p></template></div><p>outside</p>"),
                PageUrl));
    }

    [Fact]
    public void InnerHtmlDoesNotAttachADeclarativeShadowRoot()
    {
        // The entry point decides, and innerHTML is the one the Standard singles out as not setting the
        // flag. The markup stays an ordinary inert template, contents and all.
        Assert.Equal(
            "shadow=null tag=TEMPLATE contents=I|x",
            Run(
                "<div id=\"host\"></div>",
                """
                (function () {
                  var host = document.getElementById('host');
                  host.innerHTML = '<div id="inner"><template shadowrootmode="open"><i>x</i></template></div>';
                  var inner = document.getElementById('inner');
                  var t = inner.firstChild;
                  return 'shadow=' + (inner.shadowRoot === null ? 'null' : 'attached') +
                         ' tag=' + t.nodeName +
                         ' contents=' + t.content.firstChild.nodeName + '|' + t.content.firstChild.textContent;
                })()
                """));
    }
}
