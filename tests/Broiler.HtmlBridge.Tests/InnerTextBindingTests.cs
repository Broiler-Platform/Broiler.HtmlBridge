using Xunit;
using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Unit tests for <c>HTMLElement.prototype.innerText</c> and <c>HTMLElement.prototype.outerText</c>
/// setters and getters (<see cref="Broiler.HtmlBridge.Dom.Features.ElementContentBinding"/>),
/// verifying compliance with WHATWG HTML §3.2.6.2:
/// - Setting innerText replaces all children.
/// - Setting empty string or null ([LegacyNullToEmptyString]) clears children.
/// - Line breaks (\n, \r\n, \r) are converted to &lt;br&gt; elements with text nodes between them.
/// - Non-string types undergo Web IDL string conversion.
/// - Existing complex child trees are removed cleanly.
/// - Setting outerText replaces the element in its parent and merges text nodes, or throws when detached.
/// </summary>
public class InnerTextBindingTests
{
    private const string PageUrl = "https://example.test/innertext";
    private const string PageHtml = """
        <!doctype html>
        <html>
        <head><title>InnerText Tests</title></head>
        <body>
          <div id="target"><span>Initial</span> Content<!-- comment --></div>
          <div id="parent"><span id="outer-target">Original</span></div>
          <div id="empty-target"></div>
          <div id="out"></div>
        </body>
        </html>
        """;

    private static string Run(string script) => PageProbe.RunAgainst(PageHtml, PageUrl, script, decode: true);

    [Fact]
    public void InnerText_AssignSimpleString_ReplacesChildrenWithSingleTextNode()
    {
        Assert.Equal(
            "childCount=1 nodeType=3 text=Simple text textContent=Simple text innerText=Simple text",
            Run("""
                (function () {
                  var el = document.getElementById('target');
                  el.innerText = 'Simple text';
                  return 'childCount=' + el.childNodes.length +
                         ' nodeType=' + el.firstChild.nodeType +
                         ' text=' + el.firstChild.textContent +
                         ' textContent=' + el.textContent +
                         ' innerText=' + el.innerText;
                })()
                """));
    }

    [Fact]
    public void InnerText_AssignEmptyString_ClearsAllChildren()
    {
        Assert.Equal(
            "childCount=0 textContent= innerText= innerHtml=",
            Run("""
                (function () {
                  var el = document.getElementById('target');
                  el.innerText = '';
                  return 'childCount=' + el.childNodes.length +
                         ' textContent=' + el.textContent +
                         ' innerText=' + el.innerText +
                         ' innerHtml=' + el.innerHTML;
                })()
                """));
    }

    [Fact]
    public void InnerText_AssignNull_ClearsChildrenPerLegacyNullToEmptyString()
    {
        Assert.Equal(
            "childCount=0 textContent= innerText=",
            Run("""
                (function () {
                  var el = document.getElementById('target');
                  el.innerText = null;
                  return 'childCount=' + el.childNodes.length +
                         ' textContent=' + el.textContent +
                         ' innerText=' + el.innerText;
                })()
                """));
    }

    [Fact]
    public void InnerText_AssignUndefined_CoercesToStringUndefined()
    {
        Assert.Equal(
            "childCount=1 text=undefined",
            Run("""
                (function () {
                  var el = document.getElementById('target');
                  el.innerText = undefined;
                  return 'childCount=' + el.childNodes.length +
                         ' text=' + el.textContent;
                })()
                """));
    }

    [Fact]
    public void InnerText_AssignNumbersAndBooleans_CoercesToString()
    {
        Assert.Equal(
            "num=42 bool=true",
            Run("""
                (function () {
                  var el = document.getElementById('target');
                  el.innerText = 42;
                  var numText = el.textContent;
                  el.innerText = true;
                  var boolText = el.textContent;
                  return 'num=' + numText + ' bool=' + boolText;
                })()
                """));
    }

    [Fact]
    public void InnerText_SingleNewline_ProducesSingleBrElement()
    {
        Assert.Equal(
            "childCount=1 tag=BR",
            Run("""
                (function () {
                  var el = document.getElementById('target');
                  el.innerText = '\n';
                  return 'childCount=' + el.childNodes.length +
                         ' tag=' + el.firstChild.tagName;
                })()
                """));
    }

    [Fact]
    public void InnerText_LineBreaks_ProducesTextAndBrElements()
    {
        Assert.Equal(
            "childCount=7 n0=#text:a n1=BR n2=#text:b n3=BR n4=#text:c n5=BR n6=#text:d html=a<br>b<br>c<br>d",
            Run("""
                (function () {
                  var el = document.getElementById('target');
                  el.innerText = 'a\r\nb\rc\nd';
                  var res = 'childCount=' + el.childNodes.length;
                  for (var i = 0; i < el.childNodes.length; i++) {
                    var n = el.childNodes[i];
                    res += ' n' + i + '=' + n.nodeName + (n.nodeType === 3 ? ':' + n.textContent : '');
                  }
                  res += ' html=' + el.innerHTML.toLowerCase();
                  return res;
                })()
                """));
    }

    [Fact]
    public void InnerText_ConsecutiveNewlines_ProducesConsecutiveBrElements()
    {
        Assert.Equal(
            "childCount=4 n0=#text:first n1=BR n2=BR n3=#text:second",
            Run("""
                (function () {
                  var el = document.getElementById('target');
                  el.innerText = 'first\n\nsecond';
                  return 'childCount=' + el.childNodes.length +
                         ' n0=' + el.childNodes[0].nodeName + ':' + el.childNodes[0].textContent +
                         ' n1=' + el.childNodes[1].nodeName +
                         ' n2=' + el.childNodes[2].nodeName +
                         ' n3=' + el.childNodes[3].nodeName + ':' + el.childNodes[3].textContent;
                })()
                """));
    }

    [Fact]
    public void InnerText_LeadingAndTrailingNewlines_ProducesBrAtBoundaries()
    {
        Assert.Equal(
            "childCount=3 n0=BR n1=#text:middle n2=BR",
            Run("""
                (function () {
                  var el = document.getElementById('target');
                  el.innerText = '\nmiddle\n';
                  return 'childCount=' + el.childNodes.length +
                         ' n0=' + el.childNodes[0].nodeName +
                         ' n1=' + el.childNodes[1].nodeName + ':' + el.childNodes[1].textContent +
                         ' n2=' + el.childNodes[2].nodeName;
                })()
                """));
    }

    [Fact]
    public void InnerText_DetachedElement_CanBeSetAndAppended()
    {
        Assert.Equal(
            "childCount=3 html=detached<br>line attachedText=detachedline",
            Run("""
                (function () {
                  var div = document.createElement('div');
                  div.innerText = 'detached\nline';
                  var childCount = div.childNodes.length;
                  var html = div.innerHTML.toLowerCase();
                  document.body.appendChild(div);
                  return 'childCount=' + childCount + ' html=' + html + ' attachedText=' + div.textContent;
                })()
                """));
    }

    [Fact]
    public void OuterText_AssignString_ReplacesElementInParent()
    {
        Assert.Equal(
            "parentChildren=1 textContent=Replaced outerText=Replaced targetParent=null",
            Run("""
                (function () {
                  var parent = document.getElementById('parent');
                  var target = document.getElementById('outer-target');
                  target.outerText = 'Replaced';
                  return 'parentChildren=' + parent.childNodes.length +
                         ' textContent=' + parent.textContent +
                         ' outerText=' + parent.outerText +
                         ' targetParent=' + (target.parentNode ? target.parentNode.id : 'null');
                })()
                """));
    }

    [Fact]
    public void OuterText_ThrowsWhenElementHasNoParent()
    {
        Assert.Equal(
            "threw=true name=NoModificationAllowedError",
            Run("""
                (function () {
                  var detached = document.createElement('div');
                  var threw = false, name = '';
                  try {
                    detached.outerText = 'value';
                  } catch (e) {
                    threw = true;
                    name = e.name;
                  }
                  return 'threw=' + threw + ' name=' + name;
                })()
                """));
    }

    [Fact]
    public void OuterText_AssignWithNewlines_ReplacesWithTextAndBr()
    {
        Assert.Equal(
            "parentChildren=3 parentHtml=hello<br>world",
            Run("""
                (function () {
                  var parent = document.getElementById('parent');
                  var target = document.getElementById('outer-target');
                  target.outerText = 'hello\nworld';
                  return 'parentChildren=' + parent.childNodes.length +
                         ' parentHtml=' + parent.innerHTML.toLowerCase();
                })()
                """));
    }
}
