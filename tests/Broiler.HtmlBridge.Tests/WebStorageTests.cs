using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// The two Web Storage areas (HTML §12.2) — <c>localStorage</c> and <c>sessionStorage</c> — asserted
/// from page script, ahead of the retyping of the object that backs them.
/// <para>
/// An area is a legacy platform object: <c>storage.foo</c>, <c>storage['foo']</c> and
/// <c>getItem('foo')</c> are three spellings of ONE item, and <c>length</c>, <c>key(n)</c> and
/// enumeration each count that item exactly once. The mirror runs both ways, and breaking one
/// direction is silent — a page writes <c>storage.foo</c>, reads back the property it just wrote, and
/// never learns that <c>getItem</c>, <c>length</c> and <c>key()</c> stopped seeing it.
/// </para>
/// <para>
/// <b>The delete case is why the file exists now.</b> <c>Storage</c> is the only lookup-completing
/// object in the bridge whose behaviour includes a deletion, and it is right today only because the
/// backing object still overrides the engine's own delete: <c>delete localStorage.foo</c> takes the
/// item out of the store, not merely the property mirroring it. The contract the other five objects
/// have moved to declares a named read, an indexed read and a named write and no delete hook, so a
/// conversion done as it stands leaves the property deleted and the item behind — <c>getItem</c> still
/// answering, <c>length</c> still counting: a wrong answer rather than a missing feature, and nothing
/// fails at the moment it appears unless the first test below exists.
/// </para>
/// </summary>
public class WebStorageTests
{
    private const string PageUrl = "https://example.test/storage";
    private const string PageHtml = "<html><body><div id=\"out\"></div></body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture document and returns what it wrote to
    /// <c>#out</c>. Reading the result out of the serialized DOM keeps the test to the engine's public
    /// surface — the binding under test is internal, and reaching for it directly would pin its shape
    /// rather than its behaviour. Every call is a fresh page, so every area starts empty.
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
    public void DeletingAPropertyRemovesTheItemAndNotOnlyTheProperty()
    {
        // `delete storage.foo` is defined on the area, not on the object carrying the mirror: the item
        // leaves the store, getItem stops answering, length stops counting it and key(n) closes up
        // behind it. A deletion that reaches only the property passes every other test in this file.
        Assert.Equal(
            "deleted=true prop=undefined item=null length=3/2 keys=a,z key0=a key1=z",
            Run("""
                (function () {
                  localStorage.setItem('a', '1');
                  localStorage.foo = 'bar';
                  localStorage.setItem('z', '3');
                  var before = localStorage.length;
                  var result = delete localStorage.foo;
                  return 'deleted=' + result + ' prop=' + localStorage.foo +
                         ' item=' + localStorage.getItem('foo') +
                         ' length=' + before + '/' + localStorage.length +
                         ' keys=' + Object.keys(localStorage).join(',') +
                         ' key0=' + localStorage.key(0) + ' key1=' + localStorage.key(1);
                })()
                """));
    }

    [Fact]
    public void AnAreaMirrorsEverySpellingOfAnItemAndStillCountsItOnce()
    {
        // Written with setItem and read as a property, written as a property and read with getItem —
        // one item either way. Rewriting a key must not grow the area, or length reports a count nobody
        // can index into; a missing key is undefined as a property and null as an item, and feature
        // detection reads both.
        Assert.Equal(
            "empty=0 length=3 get=Z prop=Z mirrored=world index=world " +
            "k0=zulu k1=alpha k2=mike past=null missingProp=undefined missingItem=null",
            Run("""
                (function () {
                  var s = window.localStorage;
                  var empty = s.length;
                  s.setItem('zulu', 'hello');
                  s.alpha = 'world';
                  s.setItem('mike', 'm');
                  s.setItem('zulu', 'Z');
                  return 'empty=' + empty + ' length=' + s.length +
                         ' get=' + s.getItem('zulu') + ' prop=' + s.zulu +
                         ' mirrored=' + s.getItem('alpha') + ' index=' + s['alpha'] +
                         ' k0=' + s.key(0) + ' k1=' + s.key(1) + ' k2=' + s.key(2) +
                         ' past=' + s.key(3) +
                         ' missingProp=' + s.nope + ' missingItem=' + s.getItem('nope');
                })()
                """));
    }

    [Fact]
    public void RemoveItemAndClearTakeAnItemOutOfEveryViewAtOnce()
    {
        // The same removal the delete case reaches by another door, and the one that must not take the
        // interface with it: clear() empties the area and a page's very next call is usually setItem,
        // so the members have to survive being swept past.
        Assert.Equal(
            "removed=1/null/undefined cleared=0 item=null prop=undefined keys=0 key0=null methods=function",
            Run("""
                (function () {
                  localStorage.setItem('one', '1');
                  localStorage.setItem('two', '2');
                  localStorage.removeItem('one');
                  var gone = localStorage.length + '/' + localStorage.getItem('one') + '/' + localStorage.one;
                  localStorage.clear();
                  return 'removed=' + gone + ' cleared=' + localStorage.length +
                         ' item=' + localStorage.getItem('two') + ' prop=' + localStorage.two +
                         ' keys=' + Object.keys(localStorage).length +
                         ' key0=' + localStorage.key(0) +
                         ' methods=' + (typeof localStorage.setItem);
                })()
                """));
    }

    [Fact]
    public void TheTwoAreasAreSeparateStoresBehindOneInterface()
    {
        // A page stashing per-tab state in one and durable state in the other must not see either
        // answer the other's reads, and clearing one must not clear the other. Both are Storage, which
        // is the shape `instanceof` reads: a member lost from either area is a page concluding it has
        // no storage at all and taking that path.
        Assert.Equal(
            "read=local/session prop=local/session len=1/1 same=false window=true " +
            "survives=local cleared=0 storage=true",
            Run("""
                (function () {
                  localStorage.setItem('shared', 'local');
                  sessionStorage.setItem('shared', 'session');
                  var read = localStorage.getItem('shared') + '/' + sessionStorage.getItem('shared');
                  var prop = localStorage.shared + '/' + sessionStorage.shared;
                  var len = localStorage.length + '/' + sessionStorage.length;
                  sessionStorage.clear();
                  return 'read=' + read + ' prop=' + prop + ' len=' + len +
                         ' same=' + (localStorage === sessionStorage) +
                         ' window=' + (localStorage === window.localStorage) +
                         ' survives=' + localStorage.getItem('shared') +
                         ' cleared=' + sessionStorage.length +
                         ' storage=' + (localStorage instanceof Storage && sessionStorage instanceof Storage);
                })()
                """));
    }

    [Fact]
    public void AnAreaEnumeratesItsKeysAndNothingOfItsInterface()
    {
        // In a browser the members live on Storage.prototype, so an area's own keys are the stored keys
        // and only those. A member that becomes enumerable is a page iterating its own storage and
        // finding `getItem` and `length` among the values it means to sync.
        Assert.Equal(
            "keys=alpha,beta,gamma forin=alpha,beta,gamma spread=alpha,beta,gamma noLength=true noGetItem=true",
            Run("""
                (function () {
                  localStorage.setItem('alpha', '1');
                  localStorage.setItem('beta', '2');
                  localStorage.gamma = '3';
                  var keys = Object.keys(localStorage);
                  var forin = [];
                  for (var k in localStorage) { forin.push(k); }
                  var spread = Object.keys(Object.assign({}, localStorage));
                  return 'keys=' + keys.join(',') +
                         ' forin=' + forin.sort().join(',') + ' spread=' + spread.sort().join(',') +
                         ' noLength=' + (keys.indexOf('length') < 0) +
                         ' noGetItem=' + (keys.indexOf('getItem') < 0);
                })()
                """));
    }

    [Fact]
    public void AKeyNamedLikeAnInterfaceMemberDoesNotCostThePageTheMember()
    {
        // Storage has no [LegacyOverrideBuiltIns], so a key colliding with a member is stored and
        // retrievable but never exposed as a property: length stays the count, getItem stays callable.
        // Losing the guard replaces a method with a string on the object about to be called.
        Assert.Equal(
            "method=function item=shadow lengthItem=99 lengthType=number count=2 key0=getItem",
            Run("""
                (function () {
                  localStorage.setItem('getItem', 'shadow');
                  localStorage.setItem('length', '99');
                  return 'method=' + (typeof localStorage.getItem) +
                         ' item=' + localStorage.getItem('getItem') +
                         ' lengthItem=' + localStorage.getItem('length') +
                         ' lengthType=' + (typeof localStorage.length) +
                         ' count=' + localStorage.length + ' key0=' + localStorage.key(0);
                })()
                """));
    }

    [Fact(Skip = "A property assignment mirrors the raw value, not its string: WebStorageBinding.cs:223-229 " +
                 "passes `value` straight through to base.SetValue and only the backing store gets ToString(), " +
                 "so `localStorage.n = 1` reads back the number 1 where HTML §12.2.2 requires the string '1'.")]
    public void APropertyAssignmentStoresTheStringifiedValue()
    {
        // An area holds strings and nothing else, which is why `localStorage.count += 1` concatenates
        // in a browser. An area handing back the number it was given makes a page's arithmetic work
        // here and nowhere else.
        Assert.Equal(
            "type=string value=true item=42 flagType=string",
            Run("""
                (function () {
                  localStorage.count = 41 + 1;
                  localStorage.flag = true;
                  return 'type=' + (typeof localStorage.count) +
                         ' value=' + (localStorage.count === '42') +
                         ' item=' + localStorage.getItem('count') +
                         ' flagType=' + (typeof localStorage.flag);
                })()
                """));
    }

    [Fact(Skip = "A digit-only key misses the store: the overrides at WebStorageBinding.cs:223 and :232 are on " +
                 "the engine's string-key members only and a digit-only key routes to the indexed ones, so " +
                 "`localStorage[8] = 'x'` never reaches the area and setItem('7', ...) is unreachable as a property.")]
    public void ADigitOnlyKeyIsANamedPropertyLikeAnyOther()
    {
        // Storage has named property getters and setters and no indexed ones — '7' is a key like any
        // other. Pages keyed by record id write exactly this, and an item silently not being there is
        // the worst shape the failure takes.
        Assert.Equal(
            "byIndex=seven byName=seven assigned=eight length=2",
            Run("""
                (function () {
                  localStorage.setItem('7', 'seven');
                  localStorage[8] = 'eight';
                  return 'byIndex=' + localStorage[7] + ' byName=' + localStorage.getItem('7') +
                         ' assigned=' + localStorage.getItem('8') + ' length=' + localStorage.length;
                })()
                """));
    }
}
