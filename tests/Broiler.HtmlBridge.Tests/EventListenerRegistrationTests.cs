using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// <c>addEventListener</c>/<c>removeEventListener</c> registration identity, asserted from page script
/// ahead of the JSEAL retyping of the listener store.
/// <para>
/// A registration is an <c>EventListenerRegistration</c> whose listener field is engine-typed, and the
/// two operations that matter — the DOM duplicate-registration check and match-by-listener-and-capture
/// removal — are reference comparisons over that field. Retyping the record moves every one of them
/// onto a different equality, and the failure mode is quiet throughout: a listener that stops
/// deduplicating fires twice, one that stops matching on removal never comes off and fires forever.
/// </para>
/// <para>
/// <b>Identity is the subject, not firing.</b> Every assertion below is a count taken across a removal
/// that must bite and one that must not, because "the listener ran" passes just as happily when the
/// store has quietly accumulated three copies of it. The twin-function case is the sharpest: two
/// closures with identical source are one value to anything comparing structurally and two to anything
/// comparing by reference, and only the second is a browser.
/// </para>
/// </summary>
public class EventListenerRegistrationTests
{
    private const string PageUrl = "https://example.test/listeners";

    /// <summary>
    /// <c>#host</c> takes the script-registered listeners; <c>#btn</c> carries an <c>on*</c> content
    /// attribute, compiled when the element is first wrapped; <c>#log</c> is where the inline handler,
    /// which cannot see the test's closures, records that it ran.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<div id=\"host\"></div>" +
        "<div id=\"btn\" onclick=\"document.getElementById('log').textContent += 'inline;'\"></div>" +
        "<div id=\"log\"></div>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

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
    public void TheCaptureFlagIsHalfOfARegistrationsKeyInBothDirections()
    {
        // DOM §2.7 discards an add equal in listener and capture to one already registered, so the
        // spellings of "not capturing" collapse to one registration and the capturing one stands
        // beside it. Removal asks the same two questions, which is why each comes off separately: a
        // key that lost the flag would take both at the first call and nothing at the second.
        Assert.Equal(
            "deduped=1 withCapture=2 captureSurvives=1 bothGone=0",
            Run("""
                (function () {
                  var el = document.getElementById('host');
                  var n = 0;
                  var h = function () { n++; };
                  el.addEventListener('ping', h);
                  el.addEventListener('ping', h);
                  el.addEventListener('ping', h, { capture: false });
                  el.dispatchEvent(new Event('ping'));
                  var deduped = n;
                  el.addEventListener('ping', h, true);
                  el.dispatchEvent(new Event('ping'));
                  var withCapture = n - deduped;
                  el.removeEventListener('ping', h);
                  el.dispatchEvent(new Event('ping'));
                  var captureSurvives = n - deduped - withCapture;
                  el.removeEventListener('ping', h, true);
                  el.dispatchEvent(new Event('ping'));
                  return 'deduped=' + deduped + ' withCapture=' + withCapture +
                         ' captureSurvives=' + captureSurvives + ' bothGone=' + (n - deduped - withCapture - captureSurvives);
                })()
                """));
    }

    [Fact]
    public void RemovingAListenerTakesTheReferenceThatWasAdded()
    {
        // The twin is the point: same source, same behaviour, different object, and it must unregister
        // nothing. A structural or by-value comparison passes every other test in this file and fails
        // this line alone.
        Assert.Equal(
            "afterTwin=1 afterSelf=1",
            Run("""
                (function () {
                  var el = document.getElementById('host');
                  var hits = 0;
                  var make = function () { return function () { hits++; }; };
                  var a = make();
                  var twin = make();
                  el.addEventListener('ping', a);
                  el.removeEventListener('ping', twin);
                  el.dispatchEvent(new Event('ping'));
                  var afterTwin = hits;
                  el.removeEventListener('ping', a);
                  el.dispatchEvent(new Event('ping'));
                  return 'afterTwin=' + afterTwin + ' afterSelf=' + hits;
                })()
                """));
    }

    [Fact]
    public void AnObjectWithHandleEventIsAListenerMatchedByItsOwnIdentity()
    {
        // EventListener is a callback interface, so a plain object with handleEvent registers, is
        // called with itself as the receiver, deduplicates and unregisters exactly as a function does
        // — the case where the stored listener is unambiguously an object rather than a callable.
        Assert.Equal(
            "first=obj:ping once=1 afterRemove=1",
            Run("""
                (function () {
                  var el = document.getElementById('host');
                  var seen = [];
                  var obj = {
                    tag: 'obj',
                    handleEvent: function (e) { seen.push(this.tag + ':' + e.type); }
                  };
                  el.addEventListener('ping', obj);
                  el.addEventListener('ping', obj);
                  el.dispatchEvent(new Event('ping'));
                  var once = seen.length;
                  el.removeEventListener('ping', obj);
                  el.dispatchEvent(new Event('ping'));
                  return 'first=' + seen[0] + ' once=' + once + ' afterRemove=' + seen.length;
                })()
                """));
    }

    [Fact]
    public void TheWindowKeepsTheSameRegistrationRulesOnItsOwnStore()
    {
        // The window's listeners live in a different store from a node's and reach it through a
        // different call frame, so the two rules have to be asserted twice or half the surface is
        // uncovered. window.dispatchEvent fires that list alone, keeping this about the store.
        Assert.Equal(
            "deduped=1 afterRemove=1 dispatched=true",
            Run("""
                (function () {
                  var n = 0;
                  var h = function () { n++; };
                  window.addEventListener('ping', h);
                  window.addEventListener('ping', h);
                  var result = window.dispatchEvent(new Event('ping'));
                  var deduped = n;
                  window.removeEventListener('ping', h);
                  window.dispatchEvent(new Event('ping'));
                  return 'deduped=' + deduped + ' afterRemove=' + n + ' dispatched=' + result;
                })()
                """));
    }

    [Fact]
    public void AnInlineHandlerAndAnAddedListenerBothRunForOneDispatch()
    {
        // An on* content attribute is a registration too, kept in a different map from the
        // addEventListener list, and one dispatch has to reach both. Losing either is silent: the
        // page still works for whichever half survived.
        Assert.Equal(
            "inline=true added=true",
            Run("""
                (function () {
                  var btn = document.getElementById('btn');
                  var log = document.getElementById('log');
                  btn.addEventListener('click', function () { log.textContent += 'added;'; });
                  btn.dispatchEvent(new Event('click'));
                  var text = log.textContent;
                  return 'inline=' + (text.indexOf('inline;') >= 0) +
                         ' added=' + (text.indexOf('added;') >= 0);
                })()
                """));
    }

    [Fact(Skip = "Inline on* handlers fire after every addEventListener listener whenever they were " +
                 "registered: src/Broiler.HtmlBridge.Dom/Features/EventDispatchBinding.cs:152-185 runs the " +
                 "listener list first and the inline handler afterwards, where HTML §8.1.7.1 registers a " +
                 "content attribute's listener when the attribute is set — at parse time.")]
    public void AnInlineHandlerRunsBeforeAListenerAddedAfterIt()
    {
        Assert.Equal(
            "inline;added;",
            Run("""
                (function () {
                  var btn = document.getElementById('btn');
                  var log = document.getElementById('log');
                  btn.addEventListener('click', function () { log.textContent += 'added;'; });
                  btn.dispatchEvent(new Event('click'));
                  return log.textContent;
                })()
                """));
    }

    [Fact(Skip = "Event types match case-insensitively: the listener maps in " +
                 "src/Broiler.HtmlBridge.Dom/Runtime/EventTargetRegistry.cs:50 and :61 use " +
                 "StringComparer.OrdinalIgnoreCase, so a 'PING' registration answers a 'ping' dispatch " +
                 "where DOM §2.8 keys a listener on a case-sensitive type string.")]
    public void AnEventTypeIsMatchedCaseSensitively()
    {
        Assert.Equal(
            "crossCase=0 sameCase=1",
            Run("""
                (function () {
                  var el = document.getElementById('host');
                  var n = 0;
                  el.addEventListener('PING', function () { n++; });
                  el.dispatchEvent(new Event('ping'));
                  var crossCase = n;
                  el.dispatchEvent(new Event('PING'));
                  return 'crossCase=' + crossCase + ' sameCase=' + (n - crossCase);
                })()
                """));
    }
}
