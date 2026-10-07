using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Three of the causes that held Broiler.Browser at 91/100 on Acid3, each as Acid3 exercises it:
/// the table collections (tests 29, 49, 50 and 51), a computed style after a click checks a box
/// (test 43), and the script in an XHTML frame served as <c>text/xml</c> (test 80).
/// </summary>
public class Acid3RegressionTests
{
    private const string PageUrl = "https://example.test/acid3";

    private static string Run(string pageHtml, string script) =>
        PageProbe.RunAgainst(pageHtml, PageUrl, script);

    [Fact]
    public void TableCollectionsCanBeRead()
    {
        // Every read of these threw "Cannot define property": the binding redefined each array's
        // non-configurable length as an accessor.
        Assert.Equal(
            "tBodies=1 rows=2 sectionRows=1 cells=2 empty=0,0 cloneCells=1",
            Run(
                "<html><body><table id=\"t\"><thead><tr><th>h</th></tr></thead>" +
                "<tbody><tr><td>a</td><td>b</td></tr></tbody></table><div id=\"out\"></div></body></html>",
                """
                (function () {
                  var t = document.getElementById('t');
                  var empty = document.createElement('table');
                  var clone = t.cloneNode(true);
                  return 'tBodies=' + t.tBodies.length +
                         ' rows=' + t.rows.length +
                         ' sectionRows=' + t.tBodies[0].rows.length +
                         ' cells=' + t.tBodies[0].rows[0].cells.length +
                         ' empty=' + empty.tBodies.length + ',' + empty.rows.length +
                         ' cloneCells=' + clone.rows[0].cells.length;
                })()
                """));
    }

    [Fact]
    public void AComputedStyleFollowsCheckednessThatChangesOutsideTheDom()
    {
        // The first read caches the style; click(), the checked setter and a radio group's
        // exclusivity change checkedness without a DOM mutation, and the style must follow each.
        Assert.Equal(
            "before=3 click=1 uncheck=3 radio2=2,0 radio1=0,2",
            Run(
                "<html><head><style>" +
                "input { position: absolute; z-index: 0; } " +
                "#c:checked:enabled { z-index: 1; } input[type=radio]:checked { z-index: 2; } " +
                "#c:not(:checked):enabled { z-index: 3; }" +
                "</style></head><body>" +
                "<input id=\"c\" type=\"checkbox\">" +
                "<input id=\"r1\" type=\"radio\" name=\"g\"><input id=\"r2\" type=\"radio\" name=\"g\">" +
                "<div id=\"out\"></div></body></html>",
                """
                (function () {
                  function z(id) { return getComputedStyle(document.getElementById(id), '').zIndex; }
                  var c = document.getElementById('c');
                  var out = 'before=' + z('c');
                  c.click();
                  out += ' click=' + z('c');
                  c.checked = false;
                  out += ' uncheck=' + z('c');
                  z('r1'); z('r2');
                  document.getElementById('r2').click();
                  out += ' radio2=' + z('r2') + ',' + z('r1');
                  document.getElementById('r1').checked = true;
                  out += ' radio1=' + z('r2') + ',' + z('r1');
                  return out;
                })()
                """));
    }

    private const string ReadNotifications =
        "(function () {" +
        "  var d = document.getElementById('f').contentDocument;" +
        "  return d ? (window.__ran || 'none') : 'no-frame';" +
        "})()";

    private static string FramePage(string contentType, string frame) =>
        "<html><body><iframe id=\"f\" src=\"data:" + contentType + "," + frame + "\"></iframe>" +
        "<div id=\"out\"></div></body></html>";

    [Theory]
    [InlineData("text/xml")]
    [InlineData("application/xml")]
    [InlineData("application/xhtml+xml")]
    public void AnXhtmlScriptRunsWhateverXmlTypeTheFrameCameAs(string contentType)
    {
        const string frame =
            "<html xmlns='http://www.w3.org/1999/xhtml'><body><p>x</p>" +
            "<script>window.__ran = 'xhtml';</script></body></html>";

        Assert.Equal("xhtml", Run(FramePage(contentType, frame), ReadNotifications));
    }

    [Fact]
    public void AScriptOutsideTheXhtmlNamespaceDoesNotRun()
    {
        // Acid3's xhtml.3: the root, and so the script, is in http://www.w3.org/1999/xhtml#.
        const string frame =
            "<html xmlns='http://www.w3.org/1999/xhtml%23'><body>" +
            "<script>window.__ran = 'wrong-namespace';</script></body></html>";

        Assert.Equal("none", Run(FramePage("text/xml", frame), ReadNotifications));
    }

    [Fact]
    public void AScriptInAMalformedXmlFrameDoesNotRun()
    {
        // Acid3's xhtml.2: an end tag with no start tag is a well-formedness error.
        const string frame =
            "<html xmlns='http://www.w3.org/1999/xhtml'><body><p><strong/></strong></p>" +
            "<script>window.__ran = 'malformed';</script></body></html>";

        Assert.Equal("none", Run(FramePage("text/xml", frame), ReadNotifications));
    }
}
