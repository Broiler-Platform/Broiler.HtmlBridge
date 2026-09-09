using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// Constructable style sheets — <c>new CSSStyleSheet()</c>, <c>replaceSync</c> and
/// <c>document.adoptedStyleSheets</c> — asserted from page script, across the retyping of the
/// bridge's engine-typed storage.
/// <para>
/// A constructed sheet owns no element, so the only thing tying it to its rules is the object the
/// page holds: the rule list is keyed on that object, and the adopted list is a copy of the array the
/// page assigned. Both are stored in engine terms today, and both fail silently when the storage is
/// retyped wrongly — a copy that drops a member shortens a list nobody counts, and a key that stops
/// comparing by reference leaves the sheet sitting in <c>adoptedStyleSheets</c> with its rules
/// unreachable, which reads as a CSS bug and is not one.
/// </para>
/// <para>
/// <b>Identity is the whole subject, so the assertions compare objects rather than count them.</b> A
/// round trip that hands back two fresh sheets in the right order and the right number passes every
/// length check that could be written for it and applies no CSS at all.
/// </para>
/// </summary>
public class AdoptedStyleSheetTests
{
    private const string PageUrl = "https://example.test/adopted";

    /// <summary>
    /// One author style sheet, so <c>document.styleSheets</c> has a non-empty baseline an adopted
    /// sheet must not join, and one element whose computed <c>display</c> an adopted rule can move.
    /// </summary>
    private const string PageHtml =
        "<html><head><style>.marker { color: red; }</style></head>" +
        "<body><div id=\"probe\"></div><div id=\"out\"></div></body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture document and returns what it wrote to
    /// <c>#out</c>. Reading the result out of the serialized DOM keeps the test to the engine's public
    /// surface — the binding under test is internal, and reaching for it directly would pin its shape
    /// rather than its behaviour.
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
    public void AConstructedSheetIsEmptyOwnerlessAndOutsideTheDocumentsSheetList()
    {
        // CSSOM §6: a constructed sheet has no owner node and no location, and adopting it does not
        // put it in document.styleSheets — that collection is the document's own <style>/<link>
        // sheets and nothing else. The last clause is worth a line of its own, because a page walking
        // document.styleSheets to find the rules it wrote would otherwise start finding sheets that
        // belong to a component and were never in its markup.
        Assert.Equal(
            "rules=0 owner=true href=true disabled=false replaceSync=function docSheets=true",
            Run("""
                (function () {
                  var authored = document.styleSheets.length;
                  var sheet = new CSSStyleSheet();
                  var empty = sheet.cssRules.length;
                  sheet.replaceSync('.adopted { color: red }');
                  document.adoptedStyleSheets = [sheet];
                  return 'rules=' + empty +
                         ' owner=' + (sheet.ownerNode === null) +
                         ' href=' + (sheet.href === null) +
                         ' disabled=' + sheet.disabled +
                         ' replaceSync=' + (typeof sheet.replaceSync) +
                         ' docSheets=' + (document.styleSheets.length === authored);
                })()
                """));
    }

    [Fact]
    public void ReplaceSyncReplacesEveryRuleAndTheRuleListStaysTheSameShrinkingObject()
    {
        // replaceSync is a replacement rather than an append, and CSSOM §6 has it *remove* any @import
        // from what it parsed instead of rejecting the call — so the second call answers one rule, and
        // a list that only ever grows answers three. cssRules is one live object per sheet, which is
        // what makes the shrink observable: the index the deleted rule occupied has to stop existing
        // rather than linger, and each rule still has to name the sheet it came from.
        Assert.Equal(
            "sameList=true length=1 selector=#probe retired=undefined owner=true",
            Run("""
                (function () {
                  var sheet = new CSSStyleSheet();
                  sheet.replaceSync('#probe { display: inline-block } .b { color: red }');
                  var first = sheet.cssRules;
                  sheet.replaceSync('@import url(other.css); #probe { display: flex }');
                  var rules = sheet.cssRules;
                  return 'sameList=' + (rules === first) +
                         ' length=' + rules.length +
                         ' selector=' + rules[0].selectorText +
                         ' retired=' + rules[1] +
                         ' owner=' + (rules[0].parentStyleSheet === sheet);
                })()
                """));
    }

    [Fact]
    public void AdoptingSheetsHandsBackTheSameObjectsInTheSameOrder()
    {
        // The assignment copies the array the page handed over, and every member has to come back as
        // the object that went in — a sheet's rules are found by that object and by nothing else. The
        // copy also has to stay dense and stay one array: a page reads adoptedStyleSheets[1] and
        // pushes onto the value it read, and both are answered by the array the getter returns.
        Assert.Equal(
            "initial=0 isArray=true stable=true length=2 keys=0,1 first=true second=true pushed=3 last=true",
            Run("""
                (function () {
                  var a = new CSSStyleSheet(), b = new CSSStyleSheet(), c = new CSSStyleSheet();
                  var initial = document.adoptedStyleSheets.length;
                  document.adoptedStyleSheets = [a, b];
                  var read = document.adoptedStyleSheets;
                  var seen = 'initial=' + initial +
                             ' isArray=' + Array.isArray(read) +
                             ' stable=' + (document.adoptedStyleSheets === read) +
                             ' length=' + read.length +
                             ' keys=' + Object.keys(read).join(',') +
                             ' first=' + (read[0] === a) +
                             ' second=' + (read[1] === b);
                  read.push(c);
                  return seen + ' pushed=' + document.adoptedStyleSheets.length +
                                ' last=' + (document.adoptedStyleSheets[2] === c);
                })()
                """));
    }

    [Fact(Skip = "A hole is dropped instead of rejected: the assignment copies the array with the " +
                 "engine's own hole-skipping reader (DomBridge/ConstructedStyleSheets.cs:230-238, " +
                 "GetArrayElements(withHoles: false)), so [a, , b] silently becomes a two-member list " +
                 "where WebIDL converts the value to sequence<CSSStyleSheet> and throws on the " +
                 "undefined the hole yields.")]
    public void AdoptingAnArrayWithAHoleIsATypeErrorAndChangesNothing()
    {
        // THE ONE ASSERTION IN THIS FILE ABOUT THE COPY ITSELF. Everything else here survives any
        // faithful array read; the hole is where two faithful-looking readers disagree — skipping
        // holes gives length 2, walking 0..length-1 gives length 3 with an undefined in the middle —
        // and neither is what a browser does, so neither can be pinned as correct. What is correct is
        // that the conversion refuses the value before anything is stored at all.
        Assert.Equal(
            "threw=TypeError length=0",
            Run("""
                (function () {
                  var a = new CSSStyleSheet(), b = new CSSStyleSheet();
                  var threw = 'none';
                  try { document.adoptedStyleSheets = [a, , b]; }
                  catch (e) { threw = e.name; }
                  return 'threw=' + threw + ' length=' + document.adoptedStyleSheets.length;
                })()
                """));
    }

    [Fact(Skip = "An adopted sheet never reaches the live cascade: the style scope is built only from " +
                 "the <style>/<link> elements found in the tree (DomBridge.ComputedStyleEngine.cs:83-95 " +
                 "via DomBridge/Css.cs:150-167), and the adopted sheets are emitted as synthetic " +
                 "<style> elements in the serialization pass instead " +
                 "(DomBridge/ConstructedStyleSheets.cs:180-211), which getComputedStyle never runs.")]
    public void AnAdoptedSheetsRuleAppliesForExactlyAsLongAsItIsAdopted()
    {
        // The point of a constructed sheet is that it styles the document, and only while it is in
        // adoptedStyleSheets — dropping it from the list takes its rules back out of the cascade. Both
        // halves matter: a sheet that applies once and never stops is as wrong as one that never
        // applies, and only reading before, during and after says which of the two you have.
        Assert.Equal(
            "before=block during=inline-block after=block",
            Run("""
                (function () {
                  var sheet = new CSSStyleSheet();
                  sheet.replaceSync('#probe { display: inline-block }');
                  var probe = document.getElementById('probe');
                  var before = getComputedStyle(probe).display;
                  document.adoptedStyleSheets = [sheet];
                  var during = getComputedStyle(probe).display;
                  document.adoptedStyleSheets = [];
                  return 'before=' + before +
                         ' during=' + during +
                         ' after=' + getComputedStyle(probe).display;
                })()
                """));
    }

    [Fact]
    public void AConstructedSheetIsBrandedAsTheInterfaceItWasConstructedFrom()
    {
        // The branding is not the bridge's doing and that is exactly why it is asserted: the sheet is
        // built as a bare object with its members hung on it directly, and it only becomes a
        // CSSStyleSheet because [[Construct]] re-parents what a host constructor returns onto the
        // interface's prototype. Mint that sheet through anything but a construct call — a factory a
        // registration reaches for while moving the storage — and it silently stops being one, which
        // a page feature-detecting with instanceof reads as "no constructable stylesheets here".
        Assert.Equal(
            "instance=true ctor=true name=CSSStyleSheet",
            Run("""
                (function () {
                  var sheet = new CSSStyleSheet();
                  return 'instance=' + (sheet instanceof CSSStyleSheet) +
                         ' ctor=' + (sheet.constructor === CSSStyleSheet) +
                         ' name=' + sheet.constructor.name;
                })()
                """));
    }
}
