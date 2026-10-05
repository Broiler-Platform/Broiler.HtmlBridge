using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The keyboard: keys reach the focused element as <c>keydown</c>, <c>keypress</c> and <c>keyup</c>, a
/// focused text field takes typed text as the user's edit, and keys do what they do by default -- Tab
/// moves focus, Enter and Space activate, Enter in a text field submits its form -- with the events and
/// in the order Chromium has.
/// </summary>
/// <remarks>
/// <para>
/// <b>No key reached a page.</b> Nothing delivered keyboard input to the page's scripts at all, and the
/// page's own copy of a field kept the value it was loaded with.
/// </para>
/// <para>
/// The expected sequences were measured in Chromium with trusted key presses on the same structure.
/// The page records what its window hears in the capture phase as <c>type:target</c>, with the key's
/// <c>key</c>, <c>keyCode</c> and <c>charCode</c>, an edit's <c>inputType</c>, <c>data</c> and the
/// field's value, and <c>activeElement</c>.
/// </para>
/// </remarks>
public class KeyboardInputTests
{
    private const string PageUrl = "https://example.test/keys";

    private const string Recorder =
        "var log = [], pageOut = document.getElementById('out');" +
        "function nameOf(n) { return n === window ? 'window' : n === document ? 'document' : n && n.id ? n.id : n && n.nodeName ? n.nodeName.toLowerCase() : String(n); }" +
        "['keydown', 'keypress', 'keyup', 'beforeinput', 'input', 'change', 'click', 'submit', 'reset', 'focus', 'blur', 'focusin', 'focusout'].forEach(function (type) {" +
        " window.addEventListener(type, function (e) {" +
        "  var s = type + ':' + nameOf(e.target);" +
        "  if (e.key !== undefined) s += ' k=' + JSON.stringify(e.key) + ' kc=' + e.keyCode + ' cc=' + e.charCode + ' w=' + e.which;" +
        "  if (e.inputType !== undefined) s += ' it=' + e.inputType + ' d=' + JSON.stringify(e.data) + ' v=' + JSON.stringify(e.target.value) + ' c=' + e.cancelable;" +
        "  if (type === 'click') s += ' detail=' + e.detail + ' trusted=' + e.isTrusted;" +
        "  if (type === 'submit') s += ' submitter=' + nameOf(e.submitter);" +
        "  s += ' ae=' + nameOf(document.activeElement);" +
        "  log.push(s); pageOut.textContent = log.join(';'); }, true); });";

    private static InteractiveSession Start(string body, string script = "")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        var session = engine.ExecuteInteractive(
            [Recorder + script], [],
            $"<html><body>{body}<div id=\"out\"></div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    private static KeyboardInput Key(KeyboardInputKind kind, string key, string code, int keyCode, bool shift = false) =>
        new(kind, key, code) { KeyCode = keyCode, ShiftKey = shift };

    /// <summary>A key pressed and released, typing <paramref name="text"/> in between when it types something.</summary>
    private static void Press(InteractiveSession session, string key, string code, int keyCode, string? text = null, string? edited = null, bool shift = false)
    {
        session.DispatchKey(Key(KeyboardInputKind.Down, key, code, keyCode, shift));
        if (text is not null)
            session.DispatchText(new TextInput(text) { EditedValue = edited });
        session.DispatchKey(Key(KeyboardInputKind.Up, key, code, keyCode, shift));
    }

    /// <summary>What the page recorded since the last read, one event a line.</summary>
    private sealed class Recording(InteractiveSession session)
    {
        private int _read;

        public string Read()
        {
            var text = PageProbe.OutOf(session.CurrentHtml(), decode: true);
            var all = text.Length == 0 ? [] : text.Split(';');
            var entries = all.Skip(_read).ToArray();
            _read = all.Length;
            return string.Join("\n", entries);
        }
    }

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static string ValueOf(InteractiveSession session, string id)
    {
        var html = session.CurrentHtml();
        var at = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no #{id}");
        var tagStart = html.LastIndexOf('<', at);
        var tag = html[tagStart..html.IndexOf('>', at)];
        var value = tag.IndexOf("value=\"", StringComparison.Ordinal);
        return value < 0 ? string.Empty : tag[(value + 7)..tag.IndexOf('"', value + 7)];
    }

    /// <summary>
    /// A character typed in a focused text field: <c>keydown</c> with the key's code, <c>keypress</c>
    /// with the character's, <c>beforeinput</c> with the field still holding its old value,
    /// <c>input</c> with the new one, <c>keyup</c>; and the page holds the new value.
    /// </summary>
    [Fact]
    public void ACharacterTypedInAFieldIsAKeyAndAnEdit()
    {
        using var session = Start("<input id=\"field\" value=\"x\">", "document.getElementById('field').focus();");
        var recording = new Recording(session);
        recording.Read();

        Press(session, "a", "KeyA", 65, "a");

        Assert.Equal(Lines(
            "keydown:field k=\"a\" kc=65 cc=0 w=65 ae=field",
            "keypress:field k=\"a\" kc=97 cc=97 w=97 ae=field",
            "beforeinput:field it=insertText d=\"a\" v=\"x\" c=true ae=field",
            "input:field it=insertText d=\"a\" v=\"xa\" c=false ae=field",
            "keyup:field k=\"a\" kc=65 cc=0 w=65 ae=field"), recording.Read());
        Assert.Equal("xa", ValueOf(session, "field"));
    }

    /// <summary>A host's own editor made the edit: the page holds what the editor shows.</summary>
    [Fact]
    public void AHostEditorsValueIsTheFieldsValue()
    {
        using var session = Start("<input id=\"field\" value=\"bc\">", "document.getElementById('field').focus();");

        Press(session, "a", "KeyA", 65, "a", edited: "abc");

        Assert.Equal("abc", ValueOf(session, "field"));
    }

    /// <summary>
    /// A cancelled <c>keydown</c> types nothing and is no <c>keypress</c>; a cancelled <c>keypress</c>
    /// types nothing; a cancelled <c>beforeinput</c> is no <c>input</c>; the value stays, and the host is
    /// told to undo what its editor did.
    /// </summary>
    [Theory]
    [InlineData("keydown", "keydown,keyup")]
    [InlineData("keypress", "keydown,keypress,keyup")]
    [InlineData("beforeinput", "keydown,keypress,beforeinput,keyup")]
    public void ACancelledEventStopsTheEdit(string cancelled, string expected)
    {
        using var session = Start("<input id=\"field\" value=\"x\">",
            $"document.getElementById('field').focus(); window.addEventListener('{cancelled}', function (e) {{ e.preventDefault(); }});" +
            "var types = []; ['keydown', 'keypress', 'beforeinput', 'input', 'keyup'].forEach(function (t) { window.addEventListener(t, function () { types.push(t); pageOut.setAttribute('data-types', types.join(',')); }); });");

        var down = session.DispatchKey(Key(KeyboardInputKind.Down, "q", "KeyQ", 81));
        var text = session.DispatchText(new TextInput("q") { EditedValue = "xq" });
        session.DispatchKey(Key(KeyboardInputKind.Up, "q", "KeyQ", 81));

        Assert.Contains($"data-types=\"{expected}\"", session.CurrentHtml());
        Assert.Equal(cancelled == "keydown", down.DefaultPrevented);
        Assert.True(text.DefaultPrevented);
        Assert.Equal("x", ValueOf(session, "field"));
    }

    /// <summary>
    /// A backward deletion the host's editor made is <c>beforeinput</c> and <c>input</c> with no data
    /// and no <c>keypress</c>; without an editor the page takes the value's last character itself.
    /// </summary>
    [Fact]
    public void ABackwardDeletionIsAnEditWithoutAKeypress()
    {
        using var session = Start("<input id=\"field\" value=\"xy\">", "document.getElementById('field').focus();");
        var recording = new Recording(session);
        recording.Read();

        session.DispatchKey(Key(KeyboardInputKind.Down, "Backspace", "Backspace", 8));
        session.DispatchEdit(new FieldEdit("deleteContentBackward", null));
        session.DispatchKey(Key(KeyboardInputKind.Up, "Backspace", "Backspace", 8));

        Assert.Equal(Lines(
            "keydown:field k=\"Backspace\" kc=8 cc=0 w=8 ae=field",
            "beforeinput:field it=deleteContentBackward d=null v=\"xy\" c=true ae=field",
            "input:field it=deleteContentBackward d=null v=\"x\" c=false ae=field",
            "keyup:field k=\"Backspace\" kc=8 cc=0 w=8 ae=field"), recording.Read());
        Assert.Equal("x", ValueOf(session, "field"));
    }

    /// <summary>
    /// Enter in an edited text field of a form: <c>keypress</c>, <c>beforeinput</c> with no
    /// <c>input</c>, <c>change</c>, a click at the default button and its form's <c>submit</c>, which
    /// asks the host to submit the form, then <c>keyup</c>. A second Enter with nothing edited is no
    /// <c>change</c>.
    /// </summary>
    [Fact]
    public void EnterInAFieldSubmitsItsFormThroughTheDefaultButton()
    {
        using var session = Start(
            "<form id=\"form\" action=\"/search\"><input id=\"field\" name=\"q\"><button id=\"sub\">Go</button></form>",
            "document.getElementById('field').focus();");
        var recording = new Recording(session);
        Press(session, "a", "KeyA", 65, "a");
        recording.Read();

        Press(session, "Enter", "Enter", 13);

        Assert.Equal(Lines(
            "keydown:field k=\"Enter\" kc=13 cc=0 w=13 ae=field",
            "keypress:field k=\"Enter\" kc=13 cc=13 w=13 ae=field",
            "beforeinput:field it=insertLineBreak d=null v=\"a\" c=true ae=field",
            "change:field ae=field",
            "click:sub detail=0 trusted=true ae=field",
            "submit:form submitter=sub ae=field",
            "keyup:field k=\"Enter\" kc=13 cc=0 w=13 ae=field"), recording.Read());
        Assert.Equal(Dom.NavigationKind.FormSubmit, session.TakePendingNavigation()?.Kind);

        Press(session, "Enter", "Enter", 13);
        Assert.DoesNotContain("change:", recording.Read());
    }

    /// <summary>A cancelled <c>submit</c> keeps the form from being submitted.</summary>
    [Fact]
    public void ACancelledSubmitIsNotSubmitted()
    {
        using var session = Start(
            "<form id=\"form\" onsubmit=\"event.preventDefault()\"><input id=\"field\"><button id=\"sub\">Go</button></form>",
            "document.getElementById('field').focus();");

        Press(session, "Enter", "Enter", 13);

        Assert.Null(session.TakePendingNavigation());
    }

    /// <summary>A form with one text field and no submit button is submitted by Enter in it itself.</summary>
    [Fact]
    public void EnterSubmitsAFormWithNoButtonFromItsOnlyField()
    {
        using var session = Start(
            "<form id=\"form\" action=\"/search\"><input id=\"field\" name=\"q\"></form>",
            "document.getElementById('field').focus();");
        var recording = new Recording(session);
        recording.Read();

        Press(session, "Enter", "Enter", 13);

        Assert.Contains("submit:form submitter=null", recording.Read());
        Assert.Equal(Dom.NavigationKind.FormSubmit, session.TakePendingNavigation()?.Kind);
    }

    /// <summary>Enter on a focused button clicks it after <c>keypress</c>, before <c>keyup</c>.</summary>
    [Fact]
    public void EnterOnAButtonClicksIt()
    {
        using var session = Start("<button id=\"btn\" type=\"button\">B</button>", "document.getElementById('btn').focus();");
        var recording = new Recording(session);
        recording.Read();

        Press(session, "Enter", "Enter", 13);

        Assert.Equal(Lines(
            "keydown:btn k=\"Enter\" kc=13 cc=0 w=13 ae=btn",
            "keypress:btn k=\"Enter\" kc=13 cc=13 w=13 ae=btn",
            "click:btn detail=0 trusted=true ae=btn",
            "keyup:btn k=\"Enter\" kc=13 cc=0 w=13 ae=btn"), recording.Read());
    }

    /// <summary>
    /// Enter on a focused link clicks it with no <c>keypress</c>, and follows it -- the host is asked to
    /// navigate -- unless the click was cancelled.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnterOnALinkFollowsIt(bool cancelClick)
    {
        using var session = Start("<a id=\"lnk\" href=\"/next\">L</a>",
            "document.getElementById('lnk').focus();" + (cancelClick ? "document.getElementById('lnk').onclick = function (e) { e.preventDefault(); };" : string.Empty));
        var recording = new Recording(session);
        recording.Read();

        Press(session, "Enter", "Enter", 13);

        Assert.Equal(Lines(
            "keydown:lnk k=\"Enter\" kc=13 cc=0 w=13 ae=lnk",
            "click:lnk detail=0 trusted=true ae=lnk",
            "keyup:lnk k=\"Enter\" kc=13 cc=0 w=13 ae=lnk"), recording.Read());
        Assert.Equal(cancelClick ? null : "https://example.test/next", session.TakePendingNavigation()?.Url);
    }

    /// <summary>
    /// Space on a focused button clicks it as the key comes up; on a checkbox the click checks it, with
    /// <c>input</c> and <c>change</c>. The space typed is a <c>keypress</c> and nothing else.
    /// </summary>
    [Fact]
    public void SpaceClicksAButtonAndChecksACheckboxWhenItComesUp()
    {
        using var session = Start("<button id=\"btn\" type=\"button\">B</button><input id=\"chk\" type=\"checkbox\">",
            "document.getElementById('btn').focus();");
        var recording = new Recording(session);
        recording.Read();

        Press(session, " ", "Space", 32, " ");
        Assert.Equal(Lines(
            "keydown:btn k=\" \" kc=32 cc=0 w=32 ae=btn",
            "keypress:btn k=\" \" kc=32 cc=32 w=32 ae=btn",
            "keyup:btn k=\" \" kc=32 cc=0 w=32 ae=btn",
            "click:btn detail=0 trusted=true ae=btn"), recording.Read());

        session.DispatchKey(Key(KeyboardInputKind.Down, "Tab", "Tab", 9));
        session.DispatchKey(Key(KeyboardInputKind.Up, "Tab", "Tab", 9));
        recording.Read();

        Press(session, " ", "Space", 32, " ");
        Assert.Equal(Lines(
            "keydown:chk k=\" \" kc=32 cc=0 w=32 ae=chk",
            "keypress:chk k=\" \" kc=32 cc=32 w=32 ae=chk",
            "keyup:chk k=\" \" kc=32 cc=0 w=32 ae=chk",
            "click:chk detail=0 trusted=true ae=chk",
            "input:chk ae=chk",
            "change:chk ae=chk"), recording.Read());
        Assert.Contains("checked", session.CurrentHtml()[session.CurrentHtml().IndexOf("id=\"chk\"", StringComparison.Ordinal)..]);
    }

    /// <summary>
    /// Tab out of an edited text field: <c>keydown</c> there, <c>change</c> there with the body already
    /// active, the focus events, and <c>keyup</c> at the element focus reached.
    /// </summary>
    [Fact]
    public void TabMovesFocusAndFiresChangeAtTheFieldItLeaves()
    {
        using var session = Start("<input id=\"field\" value=\"x\"><textarea id=\"area\"></textarea>",
            "document.getElementById('field').focus();");
        var recording = new Recording(session);
        Press(session, "a", "KeyA", 65, "a");
        recording.Read();

        Press(session, "Tab", "Tab", 9);

        Assert.Equal(Lines(
            "keydown:field k=\"Tab\" kc=9 cc=0 w=9 ae=field",
            "change:field ae=body",
            "blur:field ae=body",
            "focusout:field ae=body",
            "focus:area ae=area",
            "focusin:area ae=area",
            "keyup:area k=\"Tab\" kc=9 cc=0 w=9 ae=area"), recording.Read());
    }

    /// <summary>
    /// The tab order: positive <c>tabindex</c> first, by it, then the rest in tree order, skipping what
    /// cannot be focused and what <c>tabindex="-1"</c> takes out; into a frame and on past it; and back
    /// with Shift+Tab. Past the last element focus leaves the page's elements, and the next Tab starts
    /// over.
    /// </summary>
    /// <remarks>
    /// Each Tab's <c>keyup</c> reaches the document focus moved to, the page's or the frame's, and
    /// either records where focus is: the frame through the page's <c>note</c>.
    /// </remarks>
    [Fact]
    public void TabFollowsTheTabOrderThroughAFrame()
    {
        using var session = Start(
            "<input id=\"a\"><div id=\"b\" tabindex=\"0\">b</div><button id=\"c\" tabindex=\"2\">c</button>" +
            "<span id=\"plain\">p</span><a id=\"d\" href=\"#\">d</a><input id=\"skip\" tabindex=\"-1\">" +
            "<iframe id=\"fr\" srcdoc=\"<input id='fin'><button id='fbtn'>x</button>" +
            "<script>document.addEventListener('keyup', function (e) { if (e.key === 'Tab') parent.note(); });</script>\"></iframe>" +
            "<button id=\"e\" tabindex=\"1\">e</button><input id=\"z\">",
            "var pageDoc = document; document.getElementById('fr').contentWindow;" +
            "function where() { var a = pageDoc.activeElement; return a && a.id === 'fr' ? 'fr/' + a.contentDocument.activeElement.id : a ? a.id || a.nodeName.toLowerCase() : 'none'; }" +
            "var seen = []; window.note = function () { seen.push(where()); pageOut.setAttribute('data-seen', seen.join(',')); };" +
            "document.addEventListener('keyup', function (e) { if (e.key === 'Tab') note(); });");

        for (var i = 0; i < 9; i++)
            Press(session, "Tab", "Tab", 9);

        Assert.Contains("data-seen=\"e,c,a,b,d,fr/fin,fr/fbtn,z,body\"", session.CurrentHtml());

        Press(session, "Tab", "Tab", 9, shift: true);
        Assert.Contains("data-seen=\"e,c,a,b,d,fr/fin,fr/fbtn,z,body,z\"", session.CurrentHtml());
    }

    /// <summary>A key with nothing focused goes to the body.</summary>
    [Fact]
    public void AKeyWithNothingFocusedGoesToTheBody()
    {
        using var session = Start("<p>text</p>");
        var recording = new Recording(session);
        recording.Read();

        Press(session, "x", "KeyX", 88, "x");

        Assert.Equal(Lines(
            "keydown:body k=\"x\" kc=88 cc=0 w=88 ae=body",
            "keypress:body k=\"x\" kc=120 cc=120 w=120 ae=body",
            "keyup:body k=\"x\" kc=88 cc=0 w=88 ae=body"), recording.Read());
    }

    /// <summary>
    /// A value a script sets on a focused field is what leaving it is measured against: leaving it with
    /// no edit since fires no <c>change</c>.
    /// </summary>
    [Fact]
    public void AScriptsValueIsNotAChangeOfTheUsers()
    {
        using var session = Start("<input id=\"field\"><button id=\"btn\">B</button>",
            "var f = document.getElementById('field'); f.focus(); f.value = 'set by script';");
        var recording = new Recording(session);
        recording.Read();

        Press(session, "Tab", "Tab", 9);

        Assert.DoesNotContain("change:", recording.Read());
    }

    /// <summary>The focused text field, for a host's editor: its type, its value, and that it is the page's.</summary>
    [Fact]
    public void TheFocusedTextFieldIsReported()
    {
        using var session = Start("<input id=\"field\" type=\"search\" value=\"v\"><button id=\"btn\">B</button>",
            "document.getElementById('field').focus();");

        var field = session.FocusedTextField;
        Assert.NotNull(field);
        Assert.Equal(("search", "v", false), (field!.InputType, field.Value, field.InFrame));

        Press(session, "Tab", "Tab", 9);
        Assert.Null(session.FocusedTextField);
    }
}
