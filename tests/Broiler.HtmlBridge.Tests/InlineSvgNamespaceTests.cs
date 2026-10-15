using Broiler.Dom;
using Broiler.Dom.Html;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Inline SVG as the bridge serializes, finds and names it, now that the Broiler.Dom.Html parser
/// builds <c>&lt;svg&gt;</c> content in the SVG namespace with its own names' case
/// (<c>foreignObject</c>, <c>linearGradient</c>) and decodes the text of an SVG <c>style</c> or
/// <c>script</c>, as HTML's rules for foreign content do.
/// <para>
/// <b>What broke.</b> Three bridge surfaces read an element's name and never its namespace. The
/// serializer wrote the text of every element named <c>style</c> or <c>script</c> raw, so a decoded
/// <c>&lt;/style&gt;&lt;img&gt;</c> inside an SVG <c>style</c> came back out as markup, and an
/// <c>innerHTML</c> round trip created the <c>img</c>. <c>getElementsByTagName</c> lowercased the
/// query and compared it ordinally, so <c>foreignObject</c> found nothing. <c>localName</c>
/// lowercased the name, answering <c>foreignobject</c>, a name no element has.
/// </para>
/// <para>
/// <b>Two kinds of test.</b> Most run on every Broiler.Dom.Html pin, because the fix they pin needs
/// no package API: a parsed SVG <c>style</c> is in the HTML namespace under 0.1.0-preview.14's
/// parser and in the SVG namespace under its successor, and the bridge answers both by the
/// namespace the element has. The ones skipped on preview.14 need the package's
/// <c>HtmlSerializationAdapter.GetNamespace</c> or its foreign-content parser, neither of which
/// preview.14 has; <c>BROILER_DOM_SERIALIZES_BY_NAMESPACE</c> (Directory.Packages.props) compiles
/// them in once the pin is past it.
/// </para>
/// <para>
/// Every assertion is what a browser answers.
/// </para>
/// </summary>
public class InlineSvgNamespaceTests
{
    private const string PageUrl = "https://example.test/inline-svg";

    private const string SvgNamespace = "http://www.w3.org/2000/svg";

#if BROILER_DOM_SERIALIZES_BY_NAMESPACE
    private const string? NeedsNamespaceAwareDom = null;
#else
    private const string NeedsNamespaceAwareDom =
        "Broiler.Dom.Html 0.1.0-preview.14 has no HtmlSerializationAdapter.GetNamespace and parses inline SVG as HTML.";
#endif

    private const string PageHtml =
        "<html><body><div id=\"h\"></div><div id=\"out\"></div></body></html>";

    /// <summary>
    /// A <c>srcdoc</c> frame with one paragraph, for the frame document's own
    /// <c>getElementsByTagName</c>.
    /// </summary>
    private const string FramePageHtml =
        "<html><body>" +
        "<iframe id=\"f\" srcdoc=\"<html><body><p id='inner'>hi</p></body></html>\"></iframe>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    private static string Run(string script) => PageProbe.RunAgainst(PageHtml, PageUrl, script, decode: true);

    [Theory]
    [InlineData("style")]
    [InlineData("script")]
    public void AParsedSvgRawTextNamedElementSurvivesAnInnerHtmlRoundTripAsText(string name)
    {
        // The mutation-XSS shape. The references are text in the markup. A parser that decodes them
        // (HTML §13.2.6.5: an SVG style or script is read in the data state) holds
        // "</style><img …>" as the element's text, and a serializer that writes that text raw hands
        // the next parse an end tag and an img. Under a parser that reads the element as raw text
        // the references stay undecoded and the round trip is an identity either way.
        Assert.Equal(
            "img=0 pwn=none kept=true",
            Run($$"""
                (function () {
                  var h = document.getElementById('h');
                  h.innerHTML = '<svg><{{name}}>&lt;/{{name}}&gt;&lt;img id=pwn src=x&gt;</{{name}}></svg>';
                  var before = h.firstChild.firstChild.textContent;
                  h.innerHTML = h.innerHTML;
                  var after = h.firstChild.firstChild;
                  return 'img=' + h.getElementsByTagName('img').length +
                         ' pwn=' + (document.getElementById('pwn') === null ? 'none' : 'found') +
                         ' kept=' + (after !== null && after.textContent === before);
                })()
                """));
    }

    [Theory]
    [InlineData("style")]
    [InlineData("script")]
    public void AScriptBuiltSvgRawTextNamedElementsTextIsEscaped(string name)
    {
        // Built through the DOM, the element is in the SVG namespace on every parser, and HTML §13.3
        // escapes its text: only an HTML style or script is raw text. Written raw, the text's own
        // end tag closed the element on the next parse and the img after it became an element.
        Assert.Equal(
            $"markup=<svg><{name}>&lt;/{name}&gt;&lt;img id=pwn src=x&gt;</{name}></svg> img=0 pwn=none",
            Run($$"""
                (function () {
                  var h = document.getElementById('h');
                  var svg = document.createElementNS('{{SvgNamespace}}', 'svg');
                  var element = document.createElementNS('{{SvgNamespace}}', '{{name}}');
                  element.textContent = '</{{name}}><img id=pwn src=x>';
                  svg.appendChild(element);
                  h.appendChild(svg);
                  var markup = h.innerHTML;
                  h.innerHTML = markup;
                  return 'markup=' + markup +
                         ' img=' + h.getElementsByTagName('img').length +
                         ' pwn=' + (document.getElementById('pwn') === null ? 'none' : 'found');
                })()
                """));
    }

    [Theory(Skip = NeedsNamespaceAwareDom)]
    [InlineData("style")]
    [InlineData("script")]
    public void AScriptBuiltSvgRawTextNamedElementsTextRoundTrips(string name)
    {
        // The escaped text decodes back to what the script wrote, because the parser reads an SVG
        // style or script in the data state. preview.14's parser reads it as raw text, so the
        // references stay in the text.
        Assert.Equal(
            "same=true img=0",
            Run($$"""
                (function () {
                  var h = document.getElementById('h');
                  var svg = document.createElementNS('{{SvgNamespace}}', 'svg');
                  var element = document.createElementNS('{{SvgNamespace}}', '{{name}}');
                  element.textContent = 'a > b && c < d </{{name}}>';
                  svg.appendChild(element);
                  h.appendChild(svg);
                  h.innerHTML = h.innerHTML;
                  return 'same=' + (h.firstChild.firstChild.textContent === 'a > b && c < d </{{name}}>') +
                         ' img=' + h.getElementsByTagName('img').length;
                })()
                """));
    }

    [Fact(Skip = NeedsNamespaceAwareDom)]
    public void OuterHtmlKeepsTheCaseOfSvgNames()
    {
        // HTML §13.3 writes an SVG element's local name as it is; only an HTML element's is
        // lowercased. Parsed and script-built SVG serialize alike.
        Assert.Equal(
            "built=<svg><linearGradient></linearGradient><foreignObject></foreignObject></svg> " +
            "parsed=<svg><linearGradient></linearGradient><foreignObject><p>x</p></foreignObject></svg>",
            Run($$"""
                (function () {
                  var svg = document.createElementNS('{{SvgNamespace}}', 'svg');
                  svg.appendChild(document.createElementNS('{{SvgNamespace}}', 'linearGradient'));
                  svg.appendChild(document.createElementNS('{{SvgNamespace}}', 'foreignObject'));
                  var h = document.getElementById('h');
                  h.innerHTML = '<svg><linearGradient></linearGradient><foreignObject><p>x</p></foreignObject></svg>';
                  return 'built=' + svg.outerHTML + ' parsed=' + h.firstChild.outerHTML;
                })()
                """));
    }

    [Fact(Skip = NeedsNamespaceAwareDom)]
    public void AnSvgElementNamedLikeAnHtmlVoidElementKeepsItsChildrenAndPrefixesAreDropped()
    {
        // Void is an HTML rule: an SVG <link> has an end tag and keeps its children. An SVG element
        // is written by its local name, so the prefix it was created with is not.
        Assert.Equal(
            "<svg><link><rect></rect></link></svg>",
            Run($$"""
                (function () {
                  var svg = document.createElementNS('{{SvgNamespace}}', 'svg');
                  var link = document.createElementNS('{{SvgNamespace}}', 'link');
                  link.appendChild(document.createElementNS('{{SvgNamespace}}', 'svg:rect'));
                  svg.appendChild(link);
                  return svg.outerHTML;
                })()
                """));
    }

    [Fact]
    public void TheBridgeUsesTheNamespaceHookWheneverThePinnedPackageHasOne()
    {
        // The hook is compiled in by a constant read off the pin's version. A pin that moved past
        // preview.14 without the constant following it leaves the bridge serializing every element
        // as HTML on a package that can do better, and nothing else would fail.
#if BROILER_DOM_SERIALIZES_BY_NAMESPACE
        const bool bridgeSetsIt = true;
#else
        const bool bridgeSetsIt = false;
#endif
        var packageHasIt = typeof(HtmlSerializationAdapter<DomNode>).GetProperty("GetNamespace") is not null;
        Assert.Equal(bridgeSetsIt, packageHasIt);
    }

    [Fact]
    public void GetElementsByTagNameLowercasesTheQueryForHtmlElementsOnly()
    {
        // DOM §4.4, "list of elements with qualified name": an HTML element matches the query
        // ASCII-lowercased, any other element the query as written. So DIV finds the page's divs,
        // and foreignObject — not foreignobject or FOREIGNOBJECT — finds the SVG element.
        Assert.Equal(
            "document=1,0,0 element=1,0,0 div=2,2",
            Run($$"""
                (function () {
                  var h = document.getElementById('h');
                  var svg = document.createElementNS('{{SvgNamespace}}', 'svg');
                  svg.appendChild(document.createElementNS('{{SvgNamespace}}', 'foreignObject'));
                  h.appendChild(svg);
                  function counts(scope) {
                    return [scope.getElementsByTagName('foreignObject').length,
                            scope.getElementsByTagName('foreignobject').length,
                            scope.getElementsByTagName('FOREIGNOBJECT').length].join(',');
                  }
                  return 'document=' + counts(document) +
                         ' element=' + counts(h) +
                         ' div=' + document.getElementsByTagName('DIV').length + ',' +
                         document.getElementsByTagName('div').length;
                })()
                """));
    }

    [Fact]
    public void GetElementsByTagNameFindsAParsedForeignObjectByItsSvgName()
    {
        // The name a page writes. Under the foreign-content parser the element is an SVG
        // foreignObject and the query matches it as written; under preview.14's it is an HTML
        // foreignobject and the query matches it lowercased.
        Assert.Equal(
            "document=1 element=1",
            Run("""
                (function () {
                  var h = document.getElementById('h');
                  h.innerHTML = '<svg><foreignObject><p>x</p></foreignObject></svg>';
                  return 'document=' + document.getElementsByTagName('foreignObject').length +
                         ' element=' + h.getElementsByTagName('foreignObject').length;
                })()
                """));
    }

    [Fact]
    public void AFramesGetElementsByTagNameMatchesQualifiedNamesAsTheDocumentsDoes()
    {
        // The frame's document has its own getElementsByTagName, and it compared without case.
        Assert.Equal(
            "1,0,0 p=1,1",
            PageProbe.RunAgainst(FramePageHtml, PageUrl, $$"""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  d.body.appendChild(d.createElementNS('{{SvgNamespace}}', 'foreignObject'));
                  return [d.getElementsByTagName('foreignObject').length,
                          d.getElementsByTagName('foreignobject').length,
                          d.getElementsByTagName('FOREIGNOBJECT').length].join(',') +
                         ' p=' + d.getElementsByTagName('P').length + ',' + d.getElementsByTagName('p').length;
                })()
                """));
    }

    [Fact]
    public void LocalNameIsTheElementsOwnLocalName()
    {
        // localName is the name as the element holds it; tagName and nodeName are the qualified
        // name, uppercased for an HTML element only.
        Assert.Equal(
            "foreignObject foreignObject foreignObject | linearGradient svg:linearGradient svg:linearGradient svg | div DIV DIV",
            Run($$"""
                (function () {
                  function names(element) {
                    return [element.localName, element.tagName, element.nodeName].join(' ');
                  }
                  var foreignObject = document.createElementNS('{{SvgNamespace}}', 'foreignObject');
                  var prefixed = document.createElementNS('{{SvgNamespace}}', 'svg:linearGradient');
                  return names(foreignObject) +
                         ' | ' + names(prefixed) + ' ' + prefixed.prefix +
                         ' | ' + names(document.createElement('div'));
                })()
                """));
    }

    [Fact(Skip = NeedsNamespaceAwareDom)]
    public void AParsedForeignObjectIsNamedAsAnSvgElement()
    {
        Assert.Equal(
            $"foreignObject foreignObject foreignObject {SvgNamespace}",
            Run("""
                (function () {
                  var h = document.getElementById('h');
                  h.innerHTML = '<svg><foreignObject></foreignObject></svg>';
                  var element = h.firstChild.firstChild;
                  return [element.localName, element.tagName, element.nodeName, element.namespaceURI].join(' ');
                })()
                """));
    }
}
