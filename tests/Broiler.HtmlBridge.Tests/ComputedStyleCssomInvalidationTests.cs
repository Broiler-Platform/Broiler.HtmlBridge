using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>getComputedStyle</c> after a page edits a <c>&lt;style&gt;</c>-backed sheet — through CSSOM
/// (<c>insertRule</c>, <c>deleteRule</c>, a write to a rule's <c>style</c>) and through the DOM (the
/// <c>&lt;style&gt;</c>'s text, or a <c>&lt;style&gt;</c> leaving the tree) — asserted from page script.
/// <para>
/// CSSOM §7.2 (<c>getComputedStyle</c>): the declaration is live, so a read after an edit answers the cascade
/// as it is then. Every <c>getComputedStyle</c> test but the control reads <c>display</c> and <c>color</c>
/// together, <em>before</em> the edit as well as after, because the defect this file was written for needs
/// both: <c>color</c> followed an <c>insertRule</c> or <c>deleteRule</c> while <c>display</c> kept the value
/// of the first read, so a page that toggled a component with
/// <c>sheet.insertRule('.panel { display: none }')</c> and checked the result saw it still showing. A test that
/// read only after the edit, or only <c>color</c>, passed against that defect. (A write to a rule's
/// <c>style</c> moved neither: it never reached the sheet at all, which <c>CssRuleStyleWriteThroughTests</c>
/// covers in depth.)
/// </para>
/// </summary>
public class ComputedStyleCssomInvalidationTests
{
    private const string PageUrl = "https://example.test/computed-style-cssom";

    /// <summary>
    /// One author sheet, <c>#author</c>, whose two rules give <c>#probe</c> a starting <c>display</c> and
    /// <c>color</c> that differ from the UA defaults, so a stale answer and a fresh one cannot coincide.
    /// <c>#framed</c> is a block with no border, for the one test that reads something other than
    /// <c>getComputedStyle</c>. <c>#holder</c> holds a sheet of its own, which styles <c>#held</c> outside it.
    /// </summary>
    private const string PageHtml =
        "<!DOCTYPE html><html><head>" +
        "<style id=\"author\">#probe { display: block; color: rgb(255, 0, 0); } " +
        "span.box { display: inline; color: rgb(0, 0, 255); }</style>" +
        "</head><body><span id=\"probe\" class=\"box\">x</span><div id=\"framed\"></div>" +
        "<div id=\"holder\"><style>#held { display: flex; color: rgb(0, 128, 0); }</style></div>" +
        "<span id=\"held\">y</span>" +
        "<div id=\"out\"></div></body></html>";

    /// <summary>
    /// <c>sheetOf</c> finds the element-backed sheet by its owner's <c>id</c>; <c>read</c> spells an element's
    /// (by default <c>#probe</c>'s) computed <c>display</c> and <c>color</c> as one token, reading a fresh
    /// declaration each time as a page would.
    /// </summary>
    private const string Helpers = """
        function sheetOf(id) {
          var list = document.styleSheets;
          for (var i = 0; i < list.length; i++) {
            var s = list[i] || list.item(i);
            if (s && s.ownerNode && s.ownerNode.id === id) return s;
          }
          return null;
        }
        function read(id) {
          var cs = getComputedStyle(document.getElementById(id || 'probe'));
          return cs.display + '/' + cs.color;
        }
        """;

    /// <summary>
    /// Runs <see cref="Helpers"/> and then <paramref name="script"/>, with a throw written to
    /// <c>#out</c> rather than propagated so an invalidation failure is asserted on directly.
    /// </summary>
    private static string Run(string script) => PageProbe.RunGuarded(PageHtml, PageUrl, Helpers, script);

    [Fact]
    public void AnInsertedRuleChangesDisplayAndColorAfterTheyWereRead()
    {
        // This used to answer before=block/rgb(255, 0, 0) after=block/rgb(0, 128, 0).
        Assert.Equal(
            "before=block/rgb(255, 0, 0) after=inline-block/rgb(0, 128, 0)",
            Run("""
                (function () {
                  var before = read();
                  sheetOf('author').insertRule('#probe { display: inline-block; color: rgb(0, 128, 0); }', 2);
                  return 'before=' + before + ' after=' + read();
                })()
                """));
    }

    [Fact]
    public void ADeletedRuleStopsApplyingToDisplayAndColorAfterTheyWereRead()
    {
        // Deleting #probe's rule leaves span.box's, which is a different display and color again. This used
        // to answer before=block/rgb(255, 0, 0) after=block/rgb(0, 0, 255).
        Assert.Equal(
            "before=block/rgb(255, 0, 0) after=inline/rgb(0, 0, 255)",
            Run("""
                (function () {
                  var before = read();
                  sheetOf('author').deleteRule(0);
                  return 'before=' + before + ' after=' + read();
                })()
                """));
    }

    [Fact]
    public void AWriteToARulesStyleChangesDisplayAndColorAfterTheyWereRead()
    {
        // Both spellings a page uses: setProperty, and the camel-cased attribute on the declaration. This
        // used to answer block/rgb(255, 0, 0) at every step, because no write reached the sheet.
        Assert.Equal(
            "before=block/rgb(255, 0, 0) setProperty=flex/rgb(0, 128, 0) attribute=none/rgb(0, 0, 0) " +
            "removed=inline/rgb(0, 0, 255)",
            Run("""
                (function () {
                  var rule = sheetOf('author').cssRules[0];
                  var before = read();
                  rule.style.setProperty('display', 'flex');
                  rule.style.setProperty('color', 'rgb(0, 128, 0)');
                  var setProperty = read();
                  rule.style.display = 'none';
                  rule.style.color = 'rgb(0, 0, 0)';
                  var attribute = read();
                  rule.style.removeProperty('display');
                  rule.style.removeProperty('color');
                  return 'before=' + before + ' setProperty=' + setProperty + ' attribute=' + attribute +
                         ' removed=' + read();
                })()
                """));
    }

    [Fact]
    public void EveryKindOfEditReachesClientTopAfterItWasRead()
    {
        // getComputedStyle was only where the stale value showed first. The same per-element memo answers
        // clientTop, hit testing, scrolling and anchor positioning, so an edit that left it standing moved
        // none of them either. clientTop is the one of those a page reads without a layout pass in
        // between, and a layout pass would have hidden the defect by clearing the memo on its own. It is
        // read after each kind of edit, and not only after an insert: if getComputedStyle ever answered
        // display without the memo, its tests would still pass for an edit that had stopped clearing it.
        Assert.Equal(
            "before=0 inserted=5 deleted=0 again=5 written=9",
            Run("""
                (function () {
                  var el = document.getElementById('framed'), s = sheetOf('author');
                  var before = el.clientTop;
                  s.insertRule('#framed { border-top: 5px solid; }', 2);
                  var inserted = el.clientTop;
                  s.deleteRule(2);
                  var deleted = el.clientTop;
                  s.insertRule('#framed { border-top: 5px solid; }', 2);
                  var again = el.clientTop;
                  s.cssRules[2].style.setProperty('border-top-width', '9px');
                  return 'before=' + before + ' inserted=' + inserted + ' deleted=' + deleted +
                         ' again=' + again + ' written=' + el.clientTop;
                })()
                """));
    }

    [Fact]
    public void ReplacingTheStyleTextChangesDisplayAndColorAfterTheyWereRead()
    {
        // Not CSSOM, but the same memo over the same sheet: the new text reaches the engine, and display
        // kept whatever the last read put in the memo — here the inserted rule's, which the new text
        // discards along with every other rule. This used to answer replaced=inline-block/rgb(0, 0, 255).
        Assert.Equal(
            "before=block/rgb(255, 0, 0) inserted=inline-block/rgb(0, 128, 0) replaced=flex/rgb(0, 0, 255)",
            Run("""
                (function () {
                  var before = read();
                  sheetOf('author').insertRule('#probe { display: inline-block; color: rgb(0, 128, 0); }', 2);
                  var inserted = read();
                  document.getElementById('author').textContent = '#probe { display: flex; color: rgb(0, 0, 255); }';
                  return 'before=' + before + ' inserted=' + inserted + ' replaced=' + read();
                })()
                """));
    }

    [Fact]
    public void AnEditToTheStyleTextNodeChangesDisplayAndColorAfterTheyWereRead()
    {
        // The character-data spelling of the same edit: no child of the <style> is added or removed. This
        // used to answer after=block/rgb(0, 128, 0).
        Assert.Equal(
            "before=block/rgb(255, 0, 0) after=grid/rgb(0, 128, 0)",
            Run("""
                (function () {
                  var before = read();
                  document.getElementById('author').firstChild.data = '#probe { display: grid; color: rgb(0, 128, 0); }';
                  return 'before=' + before + ' after=' + read();
                })()
                """));
    }

    [Fact]
    public void AStyleRemovedWithItsContainersTextStopsApplyingAfterItWasRead()
    {
        // The <style> is not the node written to, and nothing but its container's text changes. This used to
        // answer after=flex/rgb(0, 0, 0).
        Assert.Equal(
            "before=flex/rgb(0, 128, 0) after=inline/rgb(0, 0, 0)",
            Run("""
                (function () {
                  var before = read('held');
                  document.getElementById('holder').textContent = '';
                  return 'before=' + before + ' after=' + read('held');
                })()
                """));
    }

    [Fact]
    public void AnEditBeforeTheFirstReadIsSeenByIt()
    {
        // The control: with nothing read before the edit there is nothing to go stale, so this passed
        // against the defect too — it pins that the fix did not make a first read wrong.
        Assert.Equal(
            "first=inline-block/rgb(0, 128, 0)",
            Run("""
                (function () {
                  sheetOf('author').insertRule('#probe { display: inline-block; color: rgb(0, 128, 0); }', 2);
                  return 'first=' + read();
                })()
                """));
    }
}
