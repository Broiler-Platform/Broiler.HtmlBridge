using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>Node.isConnected</c> for every kind of tree a page can hold a node in — the page's document, a
/// frame's, a <c>createHTMLDocument</c> document, a shadow tree, a fragment, and no tree at all —
/// asserted from page script.
/// <para>
/// DOM §4.2.2 makes a node connected when its shadow-including root is a document, and §4.4's getter
/// answers exactly that: <em>a</em> document, not the one whose script is asking. A framed script's "am I
/// in the page yet?" guard, a component that defers work until it is attached, a library that checks
/// before measuring — each reads it about nodes in whatever document they live in, and a frame's
/// document or one a script built with <c>createHTMLDocument</c> is as much a document as the page's.
/// </para>
/// <para>
/// <b>The frame and <c>createHTMLDocument</c> rows are the ones this file exists for.</b> The getter
/// used to compare a node's root with the <em>page's</em> document, so every node in any other
/// document answered <c>false</c> — its <c>body</c> and <c>documentElement</c> included — while every
/// row about the page's own tree answered right, which is why no test of the page's tree could catch
/// it. The detached, fragment and detached-host shadow rows are the other half: a getter that answered
/// <c>true</c> for any document and any node with a parent would pass every frame and
/// <c>createHTMLDocument</c> row and fail these.
/// </para>
/// <para>
/// A shadow tree needs no special case in this bridge: its shadow root is a synthetic element parented
/// into the host, so the ordinary root walk crosses every host, nested ones included, and ends at the
/// outermost host's root — which is exactly the "shadow-including root" the Standard asks for, and the
/// shadow rows pin that it stays so.
/// </para>
/// </summary>
public class NodeIsConnectedTests
{
    private const string PageUrl = "https://example.test/is-connected";

    /// <summary>
    /// A <c>srcdoc</c> frame so the fixture needs no network (its inner attributes single-quoted so the
    /// double-quoted <c>srcdoc</c> survives), a host for the shadow rows, and <c>#out</c> for the answer.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<div id=\"page\"><span id=\"inPage\">page</span></div>" +
        "<iframe id=\"f\" srcdoc=\"<html><body><p id='inner'>hi</p></body></html>\"></iframe>" +
        "<div id=\"host\"></div>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture and returns what it wrote to <c>#out</c>, the
    /// same public-surface read <see cref="FrameDocumentTests"/> uses.
    /// </summary>
    private static string Run(string script)
    {
        var html = new ScriptEngine().Execute(
            [$"document.getElementById('out').textContent = String({script});"],
            PageHtml,
            PageUrl);

        Assert.NotNull(html);

        const string open = "<div id=\"out\">";
        var start = html!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return html[start..end];
    }

    [Fact]
    public void ANodeInThePagesDocumentIsConnected()
    {
        Assert.Equal(
            "element=true text=true body=true document=true",
            Run("""
                (function () {
                  var span = document.getElementById('inPage');
                  return 'element=' + span.isConnected +
                         ' text=' + span.firstChild.isConnected +
                         ' body=' + document.body.isConnected +
                         ' document=' + document.isConnected;
                })()
                """));
    }

    [Fact]
    public void ADetachedNodeIsNotConnectedUntilItIsInsertedAndNotAfterItIsRemoved()
    {
        // A detached subtree has parents all the way up and is still not connected: the answer is the
        // root, not the presence of a parent.
        Assert.Equal(
            "created=false childOfDetached=false text=false inserted=true removed=false",
            Run("""
                (function () {
                  var wrap = document.createElement('div'), kid = document.createElement('b');
                  wrap.appendChild(kid);
                  var text = document.createTextNode('t');
                  var answer = 'created=' + wrap.isConnected +
                               ' childOfDetached=' + kid.isConnected +
                               ' text=' + text.isConnected;
                  document.getElementById('page').appendChild(wrap);
                  answer += ' inserted=' + kid.isConnected;
                  wrap.remove();
                  return answer + ' removed=' + kid.isConnected;
                })()
                """));
    }

    [Fact]
    public void ANodeInAFramesDocumentIsConnected()
    {
        // CHANGED: every one of these answered false while the getter compared the root with the page's
        // document.
        Assert.Equal(
            "inner=true text=true body=true root=true document=true",
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  var inner = d.getElementById('inner');
                  return 'inner=' + inner.isConnected +
                         ' text=' + inner.firstChild.isConnected +
                         ' body=' + d.body.isConnected +
                         ' root=' + d.documentElement.isConnected +
                         ' document=' + d.isConnected;
                })()
                """));
    }

    [Fact]
    public void ANodeTheFramesDocumentCreatedIsConnectedOnlyWhileItIsInThatDocument()
    {
        // CHANGED, as for a frame: appended answered false while the getter compared the root with the
        // page's document.
        Assert.Equal(
            "created=false appended=true removed=false",
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  var p = d.createElement('p');
                  var answer = 'created=' + p.isConnected;
                  d.body.appendChild(p);
                  answer += ' appended=' + p.isConnected;
                  d.body.removeChild(p);
                  return answer + ' removed=' + p.isConnected;
                })()
                """));
    }

    [Fact]
    public void ANodeMovedFromThePageIntoAFrameStaysConnected()
    {
        // CHANGED, as for a frame: after answered false while the getter compared the root with the
        // page's document.
        Assert.Equal(
            "before=true after=true",
            Run("""
                (function () {
                  var d = document.getElementById('f').contentDocument;
                  var span = document.getElementById('inPage');
                  var answer = 'before=' + span.isConnected;
                  d.body.appendChild(span);
                  return answer + ' after=' + span.isConnected;
                })()
                """));
    }

    [Fact]
    public void ANodeInACreateHtmlDocumentDocumentIsConnected()
    {
        // CHANGED, as for a frame: a document a script builds is a document, so its tree is connected
        // even though no browsing context displays it.
        Assert.Equal(
            "body=true root=true document=true created=false appended=true",
            Run("""
                (function () {
                  var d = document.implementation.createHTMLDocument('t');
                  var p = d.createElement('p');
                  var answer = 'body=' + d.body.isConnected +
                               ' root=' + d.documentElement.isConnected +
                               ' document=' + d.isConnected +
                               ' created=' + p.isConnected;
                  d.body.appendChild(p);
                  return answer + ' appended=' + p.isConnected;
                })()
                """));
    }

    [Fact]
    public void ANodeInAnAttachedShadowTreeIsConnectedThroughItsHost()
    {
        // The shadow-including root of a node in a shadow tree is its host's shadow-including root
        // (DOM §4.8), so the shadow root and everything in it follow the host — through any number of
        // nested shadow trees.
        Assert.Equal(
            "inner=true root=true nestedInner=true detachedHostInner=false",
            Run("""
                (function () {
                  var root = document.getElementById('host').attachShadow({ mode: 'open' });
                  var inner = document.createElement('span');
                  root.appendChild(inner);
                  var nestedHost = document.createElement('div');
                  root.appendChild(nestedHost);
                  var nestedInner = document.createElement('i');
                  nestedHost.attachShadow({ mode: 'open' }).appendChild(nestedInner);
                  var loose = document.createElement('div').attachShadow({ mode: 'open' });
                  var looseInner = document.createElement('span');
                  loose.appendChild(looseInner);
                  return 'inner=' + inner.isConnected +
                         ' root=' + root.isConnected +
                         ' nestedInner=' + nestedInner.isConnected +
                         ' detachedHostInner=' + looseInner.isConnected;
                })()
                """));
    }

    [Fact]
    public void ANodeInADocumentFragmentIsNotConnected()
    {
        // A fragment is a root that is not a document, including a template's contents, which hang off
        // the template yet are never part of any document's tree.
        Assert.Equal(
            "fragment=false child=false templateChild=false",
            Run("""
                (function () {
                  var fragment = document.createDocumentFragment();
                  var child = document.createElement('i');
                  fragment.appendChild(child);
                  var template = document.createElement('template');
                  template.innerHTML = '<b>x</b>';
                  document.body.appendChild(template);
                  return 'fragment=' + fragment.isConnected +
                         ' child=' + child.isConnected +
                         ' templateChild=' + template.content.firstChild.isConnected;
                })()
                """));
    }
}
