using Xunit;
using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for <c>document.write</c> and <c>document.writeln</c> (<see cref="Broiler.HtmlBridge.Dom.Features.DocumentWriteBinding"/>),
/// verifying that nested scripts (such as those within table cells or divs) insert their written markup
/// directly at the parser insertion point inside the parent element, that consecutive writes maintain insertion
/// order, and that markup does not improperly escape to the end of the body.
/// </summary>
public class DocumentWriteBindingTests
{
    private const string PageUrl = "https://example.test/doc-write";

    private static string ExecutePage(string html, string url = PageUrl)
    {
        var extracted = ScriptExtractionService.ExtractAll(html, url);
        return new ScriptEngine().Execute(extracted.Scripts, extracted.DeferredScripts, html, url, extracted.ModuleRoots) ?? string.Empty;
    }

    [Fact]
    public void DocumentWrite_InTableCell_InsertsIntoTableCell()
    {
        var html = """
            <!doctype html>
            <html>
            <body>
            <table>
              <tr>
                <td id="write-cell"><script>document.write('<span id="written-value">CELL_VALUE</span>');</script></td>
                <td id="sibling-cell">SIBLING</td>
              </tr>
            </table>
            <div id="out"></div>
            <script>
              var val = document.getElementById('written-value');
              var parentId = val ? val.parentNode.id : 'null';
              var parentTag = val ? val.parentNode.tagName : 'null';
              var siblingText = document.getElementById('sibling-cell').textContent;
              document.getElementById('out').textContent = parentId + '|' + parentTag + '|' + siblingText;
            </script>
            </body>
            </html>
            """;

        var rendered = ExecutePage(html);
        Assert.Equal("write-cell|TD|SIBLING", PageProbe.OutOf(rendered));

        // Ensure written value is inside the td and not at the end of body.
        Assert.DoesNotMatch(@"CELL_VALUE\s*</body>", rendered);
    }

    [Fact]
    public void DocumentWrite_ConsecutiveWritesInSameScript_PreservesInsertionOrder()
    {
        var html = """
            <!doctype html>
            <html>
            <body>
            <table>
              <tr>
                <td id="target-cell">
                  <script>
                    document.write('<span id="first">FIRST</span>');
                    document.write('<span id="second">SECOND</span>');
                    document.write('<span id="third">THIRD</span>');
                  </script>
                </td>
              </tr>
            </table>
            <div id="out"></div>
            <script>
              var cell = document.getElementById('target-cell');
              var spans = cell.getElementsByTagName('span');
              var ids = [];
              for (var i = 0; i < spans.length; i++) ids.push(spans[i].id);
              document.getElementById('out').textContent = ids.join(',');
            </script>
            </body>
            </html>
            """;

        var rendered = ExecutePage(html);
        Assert.Equal("first,second,third", PageProbe.OutOf(rendered));
    }

    [Fact]
    public void DocumentWrite_ConsecutiveWritesWithMultipleElements_PreservesInsertionOrder()
    {
        var html = """
            <!doctype html>
            <html>
            <body>
            <div id="container">
              <script>
                document.write('<b>A</b><b>B</b>');
                document.write('<b>C</b>');
              </script>
              <span id="after">AFTER</span>
            </div>
            <div id="out"></div>
            <script>
              var c = document.getElementById('container');
              var texts = [];
              for (var i = 0; i < c.childNodes.length; i++) {
                var node = c.childNodes[i];
                if (node.tagName) {
                  if (node.tagName === 'B') texts.push('B:' + node.textContent);
                  else if (node.tagName === 'SPAN') texts.push('SPAN:' + node.textContent);
                  else texts.push(node.tagName);
                }
              }
              document.getElementById('out').textContent = texts.join('|');
            </script>
            </body>
            </html>
            """;

        var rendered = ExecutePage(html);
        // Expect SCRIPT, B:A, B:B, B:C, SPAN:AFTER in container.
        Assert.Equal("SCRIPT|B:A|B:B|B:C|SPAN:AFTER", PageProbe.OutOf(rendered));
    }

    [Fact]
    public void DocumentWrite_InterveningMutationRemovesAnchor_RecoversGracefully()
    {
        var html = """
            <!doctype html>
            <html>
            <body>
            <div id="container">
              <script>
                document.write('<span id="first">FIRST</span>');
                var f = document.getElementById('first');
                if (f) f.remove();
                document.write('<span id="second">SECOND</span>');
              </script>
            </div>
            <div id="out"></div>
            <script>
              var s = document.getElementById('second');
              document.getElementById('out').textContent = (s ? s.parentNode.id : 'missing') + '|' + (document.getElementById('first') ? 'present' : 'removed');
            </script>
            </body>
            </html>
            """;

        var rendered = ExecutePage(html);
        Assert.Equal("container|removed", PageProbe.OutOf(rendered));
    }

    [Fact]
    public void DocumentWrite_AcrossMultipleScriptsInDifferentCells_KeepsEachInOwnCell()
    {
        var html = """
            <!doctype html>
            <html>
            <body>
            <table>
              <tr>
                <td id="cell-1"><script>document.write('<span>VAL1</span>');</script></td>
                <td id="cell-2"><script>document.write('<span>VAL2</span>');</script></td>
              </tr>
            </table>
            <div id="out"></div>
            <script>
              var c1 = document.getElementById('cell-1').getElementsByTagName('span')[0].textContent;
              var c2 = document.getElementById('cell-2').getElementsByTagName('span')[0].textContent;
              document.getElementById('out').textContent = c1 + '|' + c2;
            </script>
            </body>
            </html>
            """;

        var rendered = ExecutePage(html);
        Assert.Equal("VAL1|VAL2", PageProbe.OutOf(rendered));
    }

    [Fact]
    public void DocumentWrite_MultipleArguments_ConcatenatesArguments()
    {
        var html = """
            <!doctype html>
            <html>
            <body>
            <div id="box">
              <script>
                document.write('<span id="multi">', 'hello ', 'world', '</span>');
              </script>
            </div>
            <div id="out"></div>
            <script>
              var m = document.getElementById('multi');
              document.getElementById('out').textContent = (m ? m.textContent : 'missing') + '|' + (m ? m.parentNode.id : 'null');
            </script>
            </body>
            </html>
            """;

        var rendered = ExecutePage(html);
        Assert.Equal("hello world|box", PageProbe.OutOf(rendered));
    }

    [Fact]
    public void DocumentWriteln_InsertsAtScriptPositionWithNewline()
    {
        var html = """
            <!doctype html>
            <html>
            <body>
            <div id="target">
              <script>
                document.writeln('<span id="line">LINE</span>');
              </script>
            </div>
            <div id="out"></div>
            <script>
              var span = document.getElementById('line');
              document.getElementById('out').textContent = span ? span.parentNode.id : 'missing';
            </script>
            </body>
            </html>
            """;

        var rendered = ExecutePage(html);
        Assert.Equal("target", PageProbe.OutOf(rendered));
    }

    [Fact]
    public void DocumentWrite_InHead_InsertsIntoHead()
    {
        var html = """
            <!doctype html>
            <html>
            <head>
              <script>document.write('<meta id="custom-meta" name="foo" content="bar">');</script>
            </head>
            <body>
            <div id="out"></div>
            <script>
              var meta = document.getElementById('custom-meta');
              document.getElementById('out').textContent = meta ? meta.parentNode.tagName : 'missing';
            </script>
            </body>
            </html>
            """;

        var rendered = ExecutePage(html);
        Assert.Equal("HEAD", PageProbe.OutOf(rendered));
    }

    [Fact]
    public void DocumentWrite_FromPostLoadCallback_FallsBackToBody()
    {
        var html = """
            <!doctype html>
            <html>
            <body>
            <div id="initial">INITIAL</div>
            <div id="out"></div>
            <script>
              setTimeout(function () {
                document.write('<span id="async-val">ASYNC</span>');
                var span = document.getElementById('async-val');
                document.getElementById('out').textContent = span ? span.parentNode.tagName : 'missing';
              }, 10);
            </script>
            </body>
            </html>
            """;

        using var session = new ScriptEngine().ExecuteInteractive(
            ScriptExtractionService.ExtractAll(html, PageUrl).Scripts,
            [],
            html,
            PageUrl);

        var rendered = session!.SettleLoadWindow();
        Assert.Equal("BODY", PageProbe.OutOf(rendered));
    }
}
