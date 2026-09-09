using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The CSSOM declarations that complete their own property lookup — <c>element.style</c> and
/// <c>rule.style</c> — asserted from page script, across the JSEAL exotic-object conversion.
/// <para>
/// Both used to derive from the engine's object type and override its lookup protocol; they are now
/// <c>IJsExotic</c> handlers the realm mints an object around. Nothing about that is visible to a page,
/// and this file is what says so: the two spellings of a CSS property still address one property, an
/// ordinary member still wins over the property hook, an unresolved read is still the empty string
/// rather than <c>undefined</c>, and enumeration still yields the declaration's own members and only
/// those.
/// </para>
/// <para>
/// <b>The ordering rule is why this file exists.</b> Getting it backwards — consulting the handler
/// before the object's ordinary properties — compiles, passes every test that reads a CSS property, and
/// is wrong: <c>el.style.length</c> and <c>el.style.cssText</c> would start answering the empty string
/// the CSS hook returns for a name it does not know, because those names are on the hook's exclusion
/// list and it answers for everything. So the assertions below read those members and not only the CSS
/// properties.
/// </para>
/// </summary>
public class CssOmExoticTests
{
    private const string PageUrl = "https://example.test/cssom";

    private const string PageHtml =
        "<html><head><style>p.rule { background-color: green; color: red; }</style></head>" +
        "<body><p id=\"probe\" class=\"rule\">x</p><div id=\"out\"></div></body></html>";

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
    public void AnInlineDeclarationsOrdinaryMembersWinOverItsCssPropertyLookup()
    {
        // cssText and length are accessors the declaration owns. The CSS hook answers for every name it
        // is asked about, including these, so an inverted lookup order would flatten both to "".
        Assert.Equal(
            "cssText=true length=number setProperty=function item=function",
            Run("""
                (function () {
                  var s = document.getElementById('probe').style;
                  s.cssText = 'color: red';
                  return 'cssText=' + (s.cssText.indexOf('color') >= 0) +
                         ' length=' + (typeof s.length) +
                         ' setProperty=' + (typeof s.setProperty) +
                         ' item=' + (typeof s.item);
                })()
                """));
    }

    [Fact]
    public void AnInlineDeclarationAnswersBothSpellingsOfAPropertyAndTheEmptyStringForAnything()
    {
        // The camelCase/kebab-case equivalence is the whole reason the declaration completes its own
        // lookups, and the empty string for an unresolved read is what a page's feature detection reads.
        Assert.Equal(
            "kebab=blue camel=blue unknown=true unknownType=string",
            Run("""
                (function () {
                  var s = document.getElementById('probe').style;
                  s.backgroundColor = 'blue';
                  return 'kebab=' + s['background-color'] +
                         ' camel=' + s.backgroundColor +
                         ' unknown=' + (s.notAPropertyAtAll === '') +
                         ' unknownType=' + (typeof s.notAPropertyAtAll);
                })()
                """));
    }

    [Fact]
    public void AnInlineDeclarationEnumeratesItsOwnMembersAndNothingTheHookInvents()
    {
        // Object.keys, for-in and a spread all read the same own keys. The handler supplies no names of
        // its own, so a name it merely answers for must not appear in any of them — otherwise every
        // string a page ever read off a declaration would accumulate as a key.
        Assert.Equal(
            "keys=true forin=true spread=true assigned=true invented=false",
            Run("""
                (function () {
                  var s = document.getElementById('probe').style;
                  s.color = 'red';
                  var probe = s.notAPropertyAtAll;
                  var keys = Object.keys(s);
                  var seen = [];
                  for (var k in s) { seen.push(k); }
                  var spread = Object.keys(Object.assign({}, s));
                  return 'keys=' + (keys.indexOf('setProperty') >= 0) +
                         ' forin=' + (seen.indexOf('setProperty') >= 0) +
                         ' spread=' + (spread.indexOf('setProperty') >= 0) +
                         ' assigned=' + (keys.indexOf('color') >= 0) +
                         ' invented=' + (keys.indexOf('notAPropertyAtAll') >= 0);
                })()
                """));
    }

    [Fact]
    public void ARuleDeclarationBehavesTheSameWayAsAnInlineOne()
    {
        // The second exotic: rule.style is the same handler shape over a plain property map rather than
        // the inline-style store. Indexed access and item() are both spelled here because a collection
        // may serve either, and this test is about the declaration rather than about the collection.
        Assert.Equal(
            "kebab=green camel=green cssText=true unknown=true keys=true",
            Run("""
                (function () {
                  var sheets = document.styleSheets;
                  var sheet = sheets[0] || (sheets.item ? sheets.item(0) : null);
                  var rules = sheet.cssRules;
                  var rule = rules[0] || (rules.item ? rules.item(0) : null);
                  var s = rule.style;
                  return 'kebab=' + s['background-color'] +
                         ' camel=' + s.backgroundColor +
                         ' cssText=' + (s.cssText.indexOf('background-color') >= 0) +
                         ' unknown=' + (s.notAPropertyAtAll === '') +
                         ' keys=' + (Object.keys(s).indexOf('getPropertyValue') >= 0);
                })()
                """));
    }
}
