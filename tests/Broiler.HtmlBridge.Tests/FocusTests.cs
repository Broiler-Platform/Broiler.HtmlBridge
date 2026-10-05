using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Focus: a press focuses what it lands on, <c>focus()</c> and <c>blur()</c> move it, and
/// <c>document.activeElement</c> follows -- with the events and the order Chromium has, into frames
/// and back too.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing was ever focused.</b> <c>activeElement</c> was always the body, <c>focus()</c> fired an
/// untrusted event at any element and focused nothing, and a press moved no focus at all.
/// </para>
/// <para>
/// The expected sequences were measured in Chromium with trusted clicks on the same structure. The
/// page records what its window hears in the capture phase as <c>type:target&lt;relatedTarget
/// ae=activeElement</c>; a frame records the same, prefixed <c>F:</c>, with its own
/// <c>activeElement</c> as <c>fae</c>. The layout is declared (<see cref="DeclaredFrameLayoutView"/>).
/// </para>
/// </remarks>
public class FocusTests
{
    private const string PageUrl = "https://example.test/focus";

    private static readonly Dictionary<string, System.Drawing.RectangleF> PageBoxes = new()
    {
        ["field"] = new(10, 10, 150, 20),
        ["btn"] = new(10, 40, 30, 20),
        ["tab"] = new(10, 70, 100, 40),
        ["plain"] = new(10, 120, 100, 40),
        ["frame"] = new(300, 10, 300, 200),
        ["fin"] = new(310, 20, 100, 20),
        ["fdiv"] = new(310, 60, 100, 60),
    };

    private const string Controls =
        "<input id=\"field\"><button id=\"btn\">B</button><div id=\"tab\" tabindex=\"0\">T</div><div id=\"plain\">P</div>";

    private const string Recorder =
        "var log = [], pageOut = document.getElementById('out'), pageDoc = document;" +
        "function nameOf(n) { return n === window ? 'window' : n === document ? 'document' : n && n.id ? n.id : n && n.nodeName ? n.nodeName.toLowerCase() : String(n); }" +
        "['pointerdown', 'mousedown', 'focus', 'blur', 'focusin', 'focusout', 'pointerup', 'mouseup', 'click'].forEach(function (type) {" +
        " window.addEventListener(type, function (e) {" +
        "  log.push(type + ':' + nameOf(e.target) + (e.relatedTarget ? '<' + nameOf(e.relatedTarget) : '') + ' ae=' + nameOf(document.activeElement));" +
        "  pageOut.textContent = log.join(' '); }, true); });";

    private static InteractiveSession Start(string body, string script = "")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredFrameLayoutView(PageBoxes),
        }));

        var session = engine.ExecuteInteractive(
            [Recorder + script], [],
            $"<html><body style=\"margin:0\">{body}<div id=\"out\"></div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    /// <summary>Reads what the page recorded since the last read.</summary>
    private sealed class Recording(InteractiveSession session)
    {
        private int _read;

        public string Click(double x, double y)
        {
            session.DispatchPointer(new PointerInput(PointerInputKind.Down, x, y) { Buttons = 1 });
            session.DispatchPointer(new PointerInput(PointerInputKind.Up, x, y));
            return Read();
        }

        public string Read()
        {
            var all = PageProbe.OutOf(session.CurrentHtml(), decode: true).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var entries = new List<string>();
            for (var i = _read; i < all.Length; i++)
                entries.Add(all[i]);

            _read = all.Length;

            // Each entry is two words, "type:target ae=x" (three for a frame's): rejoin them.
            return string.Join(" ", entries);
        }
    }

    /// <summary>
    /// A press focuses the field it lands on, then a button, then an element with a <c>tabindex</c>;
    /// a press on something that cannot be focused takes focus away. <c>blur</c> and
    /// <c>focusout</c> come first, with <c>activeElement</c> already the body, then <c>focus</c> and
    /// <c>focusin</c>, each naming the other element, all between <c>mousedown</c> and
    /// <c>pointerup</c>.
    /// </summary>
    [Fact]
    public void APressFocusesWhatItLandsOnInChromiumsOrder()
    {
        using var session = Start(Controls);
        var recording = new Recording(session);

        Assert.Equal(
            "pointerdown:field ae=body mousedown:field ae=body focus:field ae=field focusin:field ae=field " +
            "pointerup:field ae=field mouseup:field ae=field click:field ae=field",
            recording.Click(20, 15));
        Assert.Equal(
            "pointerdown:btn ae=field mousedown:btn ae=field blur:field<btn ae=body focusout:field<btn ae=body " +
            "focus:btn<field ae=btn focusin:btn<field ae=btn pointerup:btn ae=btn mouseup:btn ae=btn click:btn ae=btn",
            recording.Click(20, 45));
        Assert.Equal(
            "pointerdown:tab ae=btn mousedown:tab ae=btn blur:btn<tab ae=body focusout:btn<tab ae=body " +
            "focus:tab<btn ae=tab focusin:tab<btn ae=tab pointerup:tab ae=tab mouseup:tab ae=tab click:tab ae=tab",
            recording.Click(20, 80));
        Assert.Equal(
            "pointerdown:plain ae=tab mousedown:plain ae=tab blur:tab ae=body focusout:tab ae=body " +
            "pointerup:plain ae=body mouseup:plain ae=body click:plain ae=body",
            recording.Click(20, 130));
    }

    /// <summary>
    /// A press whose <c>mousedown</c> is cancelled leaves the focus where it was, and so does one
    /// whose <c>pointerdown</c> is, which then has no <c>mousedown</c> at all.
    /// </summary>
    [Theory]
    [InlineData("pointerdown")]
    [InlineData("mousedown")]
    public void ACancelledPressKeepsTheFocus(string cancelled)
    {
        using var session = Start(Controls,
            $"document.getElementById('btn').addEventListener('{cancelled}', function (e) {{ e.preventDefault(); }});" +
            "document.getElementById('field').focus();");
        var recording = new Recording(session);
        recording.Read();

        var heard = recording.Click(20, 45);

        Assert.DoesNotContain("focus:btn", heard);
        Assert.DoesNotContain("blur:field", heard);
        Assert.EndsWith("click:btn ae=field", heard);
    }

    /// <summary>
    /// <c>focus()</c> focuses what can be focused, with trusted events, and nothing else: not a plain
    /// element, a hidden one or a disabled control. <c>blur()</c> leaves the body active.
    /// <c>tabIndex</c> is 0 for what a browser focuses without one, and <c>hasFocus()</c> is true.
    /// </summary>
    [Fact]
    public void FocusAndBlurFromScriptMoveFocusAsABrowserDoes()
    {
        using var session = Start(
            Controls + "<input id=\"hidden\" style=\"display:none\"><button id=\"disabled\" disabled>D</button><a id=\"link\" href=\"#x\">L</a>",
            "var r = [], trusted = [];" +
            "var field = document.getElementById('field');" +
            "field.addEventListener('focus', function (e) { trusted.push('focus:' + e.isTrusted); });" +
            "field.addEventListener('blur', function (e) { trusted.push('blur:' + e.isTrusted); });" +
            "function active() { r.push(nameOf(document.activeElement)); }" +
            "field.focus(); active();" +
            "document.getElementById('plain').focus(); active();" +
            "document.getElementById('hidden').focus(); active();" +
            "document.getElementById('disabled').focus(); active();" +
            "field.blur(); active();" +
            "document.getElementById('link').focus(); active();" +
            "log = []; pageOut.textContent = [r.join(' '), ['link', 'btn', 'plain', 'tab'].map(function (id) { return document.getElementById(id).tabIndex; }).join(','), document.hasFocus(), trusted.join(' ')].join('|');");

        Assert.Equal(
            "field field field field body link|0,0,-1,0|true|focus:true blur:true",
            PageProbe.OutOf(session.CurrentHtml(), decode: true));
    }

    /// <summary>
    /// Into a frame and out again: the page's window gets <c>blur</c>, with the frame element already
    /// its <c>activeElement</c>, the frame's window <c>focus</c>, then the frame's input
    /// <c>focus</c>/<c>focusin</c>; a press on the frame's plain element blurs the input; a press on
    /// the page's field takes the frame window's focus away, gives the page's window its own back, and
    /// focuses the field.
    /// </summary>
    [Fact]
    public void FocusMovesIntoAFrameAndOutAgainInChromiumsOrder()
    {
        using var session = Start(
            Controls +
            "<iframe id=\"frame\" srcdoc=\"<html><body id=fbody><input id=fin><div id=fdiv>div</div><script>" +
            "[&quot;pointerdown&quot;, &quot;mousedown&quot;, &quot;focus&quot;, &quot;blur&quot;, &quot;focusin&quot;, &quot;focusout&quot;].forEach(function (type) {" +
            " window.addEventListener(type, function (e) {" +
            "  var t = e.target === window ? &quot;fwin&quot; : e.target.id;" +
            "  log.push(&quot;F:&quot; + type + &quot;:&quot; + t + (e.relatedTarget ? &quot;<&quot; + e.relatedTarget.id : &quot;&quot;) +" +
            "   &quot; fae=&quot; + document.activeElement.id + &quot; ae=&quot; + nameOf(pageDoc.activeElement));" +
            "  pageOut.textContent = log.join(&quot; &quot;); }, true); });" +
            "</script></body></html>\"></iframe>",
            "document.getElementById('frame').contentWindow;");
        var recording = new Recording(session);
        recording.Read();

        Assert.Equal(
            "F:pointerdown:fin fae=fbody ae=body F:mousedown:fin fae=fbody ae=body blur:window ae=frame " +
            "F:focus:fwin fae=fbody ae=frame F:focus:fin fae=fin ae=frame F:focusin:fin fae=fin ae=frame",
            recording.Click(320, 25));
        Assert.Equal(
            "F:pointerdown:fdiv fae=fin ae=frame F:mousedown:fdiv fae=fin ae=frame " +
            "F:blur:fin fae=fbody ae=frame F:focusout:fin fae=fbody ae=frame",
            recording.Click(320, 80));
        Assert.Equal(
            "pointerdown:field ae=frame mousedown:field ae=frame F:blur:fwin fae=fbody ae=body focus:window ae=body " +
            "focus:field ae=field focusin:field ae=field pointerup:field ae=field mouseup:field ae=field click:field ae=field",
            recording.Click(20, 15));
    }

    /// <summary>
    /// A frame's document has an <c>activeElement</c> and <c>hasFocus()</c> of its own: its body and
    /// false until focus moves into it, then its focused element and true, while the page's
    /// <c>activeElement</c> is the frame element. A focused element that is removed leaves the body
    /// active. Frame documents had neither member.
    /// </summary>
    [Fact]
    public void AFramesDocumentAnswersItsOwnActiveElement()
    {
        using var session = Start(
            Controls + "<iframe id=\"frame\" srcdoc=\"<body id=fbody><input id=fin></body>\"></iframe>",
            "var frameDoc = document.getElementById('frame').contentDocument, r = [];" +
            "r.push(frameDoc.activeElement.id, frameDoc.hasFocus(), document.hasFocus());" +
            "frameDoc.getElementById('fin').focus();" +
            "r.push(nameOf(document.activeElement), frameDoc.activeElement.id, frameDoc.hasFocus(), document.hasFocus());" +
            "var field = document.getElementById('field'); field.focus(); field.remove();" +
            "r.push(nameOf(document.activeElement), frameDoc.hasFocus());" +
            "log = []; pageOut.textContent = r.join('|');");

        Assert.Equal(
            "fbody|false|true|frame|fin|true|true|body|false",
            PageProbe.OutOf(session.CurrentHtml(), decode: true));
    }
}
