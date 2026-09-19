using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>addEventListener</c>/<c>removeEventListener</c> registration identity, asserted from page script
/// across the JSEAL retyping of the listener store.
/// <para>
/// A registration is an <c>EventListenerRegistration</c> whose listener field is a JSEAL handle, and the
/// duplicate-registration check and explicit removal compare that handle and the capture flag.
/// Individual registrations have their own identity and shared removal state so a dispatch snapshot
/// observes removal without mistaking a later re-registration for the original listener.
/// </para>
/// <para>
/// <b>Identity is the subject, not firing.</b> Every assertion below is taken across a removal that
/// must bite and one that must not, because "the listener ran" passes just as happily when the store
/// has quietly accumulated three copies of it. The twin-function case is the sharpest: two closures
/// with identical source are one value to anything comparing structurally and two to anything
/// comparing by reference, and only the second is a browser.
/// </para>
/// </summary>
public partial class EventListenerRegistrationTests
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

    [Fact]
    public void TheOnclickReflectorAnswersTheStoredHandlerAndClearsOnANonFunction()
    {
        // element.onclick reads and writes the same per-element map the on* content attribute is
        // compiled into and dispatch fires from. The getter used to mint a fresh handle over the stored
        // function on every read and now returns the stored one; either way a page must see one object,
        // and === is the engine's comparison of the objects themselves, not the bridge's of its handles.
        Assert.Equal(
            "compiled=function stable=true assigned=true cleared=null",
            Run("""
                (function () {
                  var btn = document.getElementById('btn');
                  var compiled = typeof btn.onclick;
                  var stable = btn.onclick === btn.onclick;
                  var f = function () {};
                  btn.onclick = f;
                  var assigned = btn.onclick === f;
                  btn.onclick = 5;
                  return 'compiled=' + compiled + ' stable=' + stable +
                         ' assigned=' + assigned + ' cleared=' + btn.onclick;
                })()
                """));
    }

    [Fact]
    public void AnAssignedHandlerAndASetAttributeHandlerEachFireFromTheSameMap()
    {
        // The two writers the parse-time compile does not cover: a script assignment, which stores the
        // argument the reflector was handed, and setAttribute, which compiles through the attribute
        // path instead of the wrapper path. Each replaces the entry the one before it made, so the log
        // names exactly which handler dispatch found, and that it was called with the event.
        Assert.Equal(
            "assigned:click;set:click;",
            Run("""
                (function () {
                  var btn = document.getElementById('btn');
                  var log = document.getElementById('log');
                  btn.onclick = function (e) { log.textContent += 'assigned:' + e.type + ';'; };
                  btn.dispatchEvent(new Event('click'));
                  btn.setAttribute('onclick', "document.getElementById('log').textContent += 'set:' + event.type + ';'");
                  btn.dispatchEvent(new Event('click'));
                  return log.textContent;
                })()
                """));
    }

    [Fact]
    public void AOnceListenerTakesItsOwnRegistrationAndNotTheSameFunctionCapturing()
    {
        // One function registered twice -- capturing, and as a once-listener -- is two registrations,
        // and firing the second must take exactly that one out of the list. At the target the
        // capturing one runs first, so the order says which is left: a removal that matched on the
        // listener alone would take the capturing registration instead, and the middle run would read
        // 'abc'.
        Assert.Equal(
            "babc|bac|ac",
            Run("""
                (function () {
                  var el = document.getElementById('host');
                  var seen = '';
                  var a = function () { seen += 'a'; };
                  var b = function () { seen += 'b'; };
                  var c = function () { seen += 'c'; };
                  el.addEventListener('ping', a);
                  el.addEventListener('ping', b, true);
                  el.addEventListener('ping', b, { once: true });
                  el.addEventListener('ping', c);
                  el.dispatchEvent(new Event('ping'));
                  seen += '|';
                  el.dispatchEvent(new Event('ping'));
                  seen += '|';
                  el.removeEventListener('ping', b, true);
                  el.dispatchEvent(new Event('ping'));
                  return seen;
                })()
                """));
    }

    [Fact]
    public void EverySpellingOfTheOptionsArgumentDecidesCaptureTheWayTheLanguageDoes()
    {
        // Capture is read from a dictionary when options is an object and coerced when it is not, and
        // both answers have to be the language's. The listener on #host runs before its child's when
        // it captures ('HT') and after it when it does not ('TH'); removing it with the same spelling
        // must leave only the child's ('T'). The falsy and truthy spellings must differ, which is what
        // a constant answer fails. 0n is the row that separates the realm's coercion from the handle's
        // own truthiness, which is true for every BigInt. The events are plain objects so that the row
        // measures the store and nothing about the Event constructor.
        Assert.Equal(
            "omitted=TH/T undefined=TH/T null=TH/T false=TH/T true=HT/T 0=TH/T NaN=TH/T empty=TH/T " +
            "x=HT/T 0n=TH/T 1n=HT/T {}=TH/T {capture:true}=HT/T {capture:false}=TH/T " +
            "{capture:0n}=TH/T {capture:1}=HT/T",
            Run("""
                (function () {
                  var host = document.getElementById('host');
                  var child = document.createElement('span');
                  host.appendChild(child);
                  function probe(args) {
                    var seen = '';
                    var h = function () { seen += 'H'; };
                    var t = function () { seen += 'T'; };
                    child.addEventListener('ping', t);
                    host.addEventListener.apply(host, ['ping', h].concat(args));
                    child.dispatchEvent({ type: 'ping', bubbles: true });
                    seen += '/';
                    host.removeEventListener.apply(host, ['ping', h].concat(args));
                    child.dispatchEvent({ type: 'ping', bubbles: true });
                    child.removeEventListener('ping', t);
                    return seen;
                  }
                  var rows = [
                    ['omitted', []], ['undefined', [undefined]], ['null', [null]], ['false', [false]],
                    ['true', [true]], ['0', [0]], ['NaN', [NaN]], ['empty', [""]], ['x', ["x"]],
                    ['0n', [0n]], ['1n', [1n]], ['{}', [{}]], ['{capture:true}', [{ capture: true }]],
                    ['{capture:false}', [{ capture: false }]], ['{capture:0n}', [{ capture: 0n }]],
                    ['{capture:1}', [{ capture: 1 }]]
                  ];
                  return rows.map(function (row) { return row[0] + '=' + probe(row[1]); }).join(' ');
                })()
                """));
    }

    [Fact]
    public void AnOptionsObjectIsReadOncePerFlagInOrderAndNotAtAllWithoutAList()
    {
        // A page sees every read of its options object through a getter. Adding reads capture, once
        // and passive in that order, even for an add the duplicate check then discards; removing
        // reads capture alone; and removing from a type with no list reads nothing, because the list
        // is looked up first. A re-materialising conversion would read none of them, and a doubled
        // read would show twice.
        Assert.Equal(
            "copcop|c|",
            Run("""
                (function () {
                  var el = document.getElementById('host');
                  var log = '';
                  var options = {
                    get capture() { log += 'c'; return false; },
                    get once() { log += 'o'; return false; },
                    get passive() { log += 'p'; return false; }
                  };
                  var h = function () {};
                  el.addEventListener('ping', h, options);
                  el.addEventListener('ping', h, options);
                  log += '|';
                  el.removeEventListener('ping', h, options);
                  log += '|';
                  el.removeEventListener('pong', h, options);
                  return log;
                })()
                """));
    }

    [Fact]
    public void AMessagePortKeepsTheSameRegistrationRulesAndCallsOnlyACallableHandler()
    {
        // A port's listeners live in the generic-target store, not a node's or the window's, and are
        // reached through a third call frame, so the rules are asserted a third time. Its on<type>
        // handler is read off the port itself: a number there is not called and does not throw, and a
        // function is called.
        Assert.Equal(
            "deduped=1 afterRemove=1 primitiveHandler=0 functionHandler=1",
            Run("""
                (function () {
                  var port = new MessageChannel().port1;
                  var n = 0;
                  var h = function () { n++; };
                  port.addEventListener('ping', h);
                  port.addEventListener('ping', h);
                  port.dispatchEvent(new Event('ping'));
                  var deduped = n;
                  port.removeEventListener('ping', h);
                  port.dispatchEvent(new Event('ping'));
                  var m = 0;
                  port.onping = 5;
                  port.dispatchEvent(new Event('ping'));
                  var primitiveHandler = m;
                  port.onping = function () { m++; };
                  port.dispatchEvent(new Event('ping'));
                  return 'deduped=' + deduped + ' afterRemove=' + n +
                         ' primitiveHandler=' + primitiveHandler + ' functionHandler=' + m;
                })()
                """));
    }

    [Fact(Skip = "Inline on* handlers fire after every addEventListener listener whenever they were " +
                 "registered: src/Broiler.HtmlBridge.Dom/Features/EventDispatchBinding.cs:149-180 runs the " +
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
                 "src/Broiler.HtmlBridge.Dom/Runtime/EventTargetRegistry.cs:46 and :57 use " +
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
