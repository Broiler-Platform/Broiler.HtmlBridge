using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A text field's selection -- <c>selectionStart</c>, <c>setSelectionRange()</c>, <c>select()</c>,
/// <c>setRangeText()</c>, the <c>select</c> and <c>selectionchange</c> events, the selection a host's
/// editor reports -- and an input method's composition in it.
/// </summary>
/// <remarks>
/// <para>
/// <b>None of the selection API existed</b>: <c>selectionStart</c> was <c>undefined</c> and
/// <c>setSelectionRange()</c> a <c>TypeError</c>. <b>Nor did composition</b>: a page heard nothing of an
/// input method.
/// </para>
/// <para>
/// The selection's behaviour was measured in Chromium, with scripts and with trusted key presses and
/// drags: a script's change fires only a coalesced <c>selectionchange</c> at the field, the user's
/// selecting fires a trusted <c>select</c> too, and a Tab into an input selects all of it. The
/// composition's order is the UI Events and Input Events specifications', as Chromium implements them;
/// nothing drives an input method in the browser the rest was measured in.
/// </para>
/// </remarks>
public class FieldSelectionTests
{
    private const string PageUrl = "https://example.test/selection";

    private const string Recorder =
        "var pageOut = document.getElementById('out'), log = [];" +
        "var t = document.getElementById('t'), ta = document.getElementById('ta');" +
        "function sel(f) { return [f.selectionStart, f.selectionEnd, f.selectionDirection].join(','); }" +
        "['select', 'selectionchange', 'compositionstart', 'compositionupdate', 'compositionend', 'beforeinput', 'input', 'change', 'keydown'].forEach(function (type) {" +
        "  window.addEventListener(type, function (e) {" +
        "    if (e.key === 'F9' || e.key === 'F8') return;" +
        "    var s = type + ':' + (e.target.id || e.target.nodeName);" +
        "    if (e.data !== undefined) s += ' d=' + JSON.stringify(e.data);" +
        "    if (e.inputType !== undefined) s += ' it=' + e.inputType + ' c=' + e.cancelable;" +
        "    if (e.isComposing !== undefined) s += ' ic=' + e.isComposing;" +
        "    if (type === 'select') s += ' trusted=' + e.isTrusted + ' bubbles=' + e.bubbles;" +
        "    if (e.target.value !== undefined) s += ' v=' + JSON.stringify(e.target.value);" +
        "    log.push(s); }, true); });" +
        "document.addEventListener('keyup', function (e) { if (e.key === 'F9') { pageOut.textContent = log.join(';'); log = []; } }, true);";

    private static InteractiveSession Start(string script = "", string body =
        "<input id=\"t\" value=\"hello\"><textarea id=\"ta\">line</textarea><input id=\"em\" type=\"email\" value=\"a@b.c\"><input id=\"nu\" type=\"number\" value=\"12\">")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        var session = engine.ExecuteInteractive(
            [Recorder + script], [], $"<html><body>{body}<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    /// <summary>What the page recorded since it last answered.</summary>
    private static string Ask(InteractiveSession session)
    {
        session.SettleLoadWindow();
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, "F9", "F9") { KeyCode = 120 });
        return PageProbe.OutOf(session.CurrentHtml(), decode: true);
    }

    private static void Run(InteractiveSession session) =>
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "F8", "F8") { KeyCode = 119 });

    private static void Key(InteractiveSession session, string key, int keyCode, bool shift = false)
    {
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, key, key) { KeyCode = keyCode, ShiftKey = shift });
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, key, key) { KeyCode = keyCode, ShiftKey = shift });
    }

    /// <summary>Every answer of the selection API, as Chromium gives it for the same calls.</summary>
    [Fact]
    public void TheSelectionApiAnswersAsChromiumDoes()
    {
        using var session = Start(
            "var em = document.getElementById('em'), nu = document.getElementById('nu'), out = [];" +
            "function attempt(f) { try { f(); return 'ok'; } catch (e) { return e.name; } }" +
            "out.push(sel(t), sel(ta), JSON.stringify([em.selectionStart, em.selectionEnd, em.selectionDirection]));" +
            "out.push(attempt(function () { em.setSelectionRange(0, 1); }), attempt(function () { em.selectionStart = 1; }), attempt(function () { em.select(); }));" +
            "out.push(JSON.stringify([nu.selectionStart, nu.selectionEnd]));" +
            "t.value = 'abcdef'; out.push(sel(t));" +
            "t.setSelectionRange(1, 3); out.push(sel(t));" +
            "t.setSelectionRange(4, 2, 'backward'); out.push(sel(t));" +
            "t.setSelectionRange(5, 1); out.push(sel(t));" +
            "t.setSelectionRange(2, 99, 'nonsense'); out.push(sel(t));" +
            "t.select(); out.push(sel(t));" +
            "t.selectionStart = 2; out.push(sel(t));" +
            "t.selectionEnd = 1; out.push(sel(t));" +
            "t.selectionDirection = 'backward'; out.push(sel(t));" +
            "t.value = 'abcdef'; t.setSelectionRange(1, 3);" +
            "t.setRangeText('XY'); out.push(t.value + ' ' + sel(t));" +
            "t.setRangeText('Q', 0, 1, 'select'); out.push(t.value + ' ' + sel(t));" +
            "t.setRangeText('ZZ', 2, 2, 'end'); out.push(t.value + ' ' + sel(t));" +
            "t.setRangeText('W', 0, 3, 'start'); out.push(t.value + ' ' + sel(t));" +
            "out.push(attempt(function () { t.setRangeText('x', 5, 2); }));" +
            "pageOut.setAttribute('data-api', out.join('|'));");

        var html = session.CurrentHtml();
        Assert.Contains(
            "data-api=\"0,0,forward|0,0,forward|[null,null,null]|InvalidStateError|InvalidStateError|ok|[null,null]" +
            "|6,6,forward|1,3,forward|2,2,backward|1,1,forward|2,6,forward|0,6,forward|2,6,forward|1,1,forward|1,1,forward" +
            "|aXYdef 1,3,forward|QXYdef 0,1,forward|QXZZYdef 4,4,forward|WZYdef 0,0,forward|IndexSizeError\"",
            html);
    }

    /// <summary>
    /// A script's selection changes fire no <c>select</c>, and one <c>selectionchange</c> at the field in a
    /// later task however many came in one; a change to the selection it already has fires nothing.
    /// </summary>
    [Fact]
    public void AScriptsChangesFireOneSelectionChangeAndNoSelect()
    {
        using var session = Start(
            "document.addEventListener('keydown', function (e) {" +
            "  if (e.key !== 'F8') return;" +
            "  if (!window.again) { window.again = true; t.setSelectionRange(1, 3); }" +
            "  t.setSelectionRange(2, 4); log.push('sync ' + sel(t)); }, true);");

        Run(session);
        Assert.Equal("sync 2,4,forward;selectionchange:t v=\"hello\"", Ask(session));

        Run(session);
        Assert.Equal("sync 2,4,forward", Ask(session));
    }

    /// <summary>
    /// The user's selection, which the host's editor reports, fires a trusted <c>select</c> when it selects
    /// something new and a <c>selectionchange</c>; the page's properties follow it, direction included.
    /// </summary>
    [Fact]
    public void TheUsersSelectionFiresSelect()
    {
        using var session = Start("t.focus();");
        Ask(session);

        session.DispatchSelection(new FieldSelectionInput(1, 4) { Backward = true });
        Assert.Equal("select:t trusted=true bubbles=true v=\"hello\";selectionchange:t v=\"hello\"", Ask(session));
        Assert.Equal(1, session.FocusedTextField?.SelectionStart);
        Assert.Equal(4, session.FocusedTextField?.SelectionEnd);
        Assert.True(session.FocusedTextField?.SelectionBackward);

        session.DispatchSelection(new FieldSelectionInput(5, 5));
        Assert.Equal("selectionchange:t v=\"hello\"", Ask(session));
    }

    /// <summary>A Tab into an input selects all of it; into a text area keeps its selection.</summary>
    [Fact]
    public void ATabIntoAnInputSelectsAllOfIt()
    {
        using var session = Start("document.addEventListener('keyup', function (e) { if (e.key === 'Tab') log.push(document.activeElement.id + ' ' + sel(document.activeElement)); }, true); ta.setSelectionRange(1, 1);");
        Ask(session);

        Key(session, "Tab", 9);
        Key(session, "Tab", 9);
        var heard = Ask(session).Split(';').Where(entry => !entry.StartsWith("keydown:", StringComparison.Ordinal));
        Assert.Equal("t 0,5,forward;ta 1,1,forward;selectionchange:t v=\"hello\";selectionchange:ta v=\"line\"", string.Join(";", heard));
    }

    /// <summary>
    /// The host's editor learns what a script did to the focused field: its selection is in
    /// <c>FocusedTextField</c>, and <c>FieldVersion</c> moves when a script changed it, not when the
    /// host's own selection reached the page.
    /// </summary>
    [Fact]
    public void FieldVersionFollowsWhatAScriptDoes()
    {
        using var session = Start(
            "t.focus();" +
            "document.addEventListener('keydown', function (e) { if (e.key === 'F8') { t.value = 'HELLO WORLD'; t.setSelectionRange(0, 5); } }, true);");

        var before = session.FieldVersion;
        session.DispatchSelection(new FieldSelectionInput(2, 2));
        Assert.Equal(before, session.FieldVersion);

        Run(session);
        Assert.NotEqual(before, session.FieldVersion);
        Assert.Equal("HELLO WORLD", session.FocusedTextField?.Value);
        Assert.Equal(0, session.FocusedTextField?.SelectionStart);
        Assert.Equal(5, session.FocusedTextField?.SelectionEnd);
    }

    /// <summary>
    /// A composition: <c>compositionstart</c>, then for each change <c>compositionupdate</c> and an
    /// <c>insertCompositionText</c> edit -- a <c>beforeinput</c> that cannot be cancelled and an
    /// <c>input</c>, composing -- the field holding what is being composed; the commit's edit, then
    /// <c>compositionend</c>. Keys pressed while composing are composing too.
    /// </summary>
    [Fact]
    public void ACompositionIsHeardInTheSpecificationsOrder()
    {
        using var session = Start("t.focus(); t.setSelectionRange(5, 5);");
        Ask(session);

        session.DispatchComposition(new CompositionInput(CompositionInputKind.Start, string.Empty));
        session.DispatchComposition(new CompositionInput(CompositionInputKind.Update, "k"));
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "Process", "KeyA") { KeyCode = 229 });
        session.DispatchComposition(new CompositionInput(CompositionInputKind.Update, "か"));
        session.DispatchComposition(new CompositionInput(CompositionInputKind.Commit, "か"));

        Assert.Equal(string.Join(";",
            "compositionstart:t d=\"\" v=\"hello\"",
            "compositionupdate:t d=\"k\" v=\"hello\"",
            "beforeinput:t d=\"k\" it=insertCompositionText c=false ic=true v=\"hello\"",
            "input:t d=\"k\" it=insertCompositionText c=false ic=true v=\"hellok\"",
            "keydown:t ic=true v=\"hellok\"",
            "compositionupdate:t d=\"か\" v=\"hellok\"",
            "beforeinput:t d=\"か\" it=insertCompositionText c=false ic=true v=\"hellok\"",
            "input:t d=\"か\" it=insertCompositionText c=false ic=true v=\"helloか\"",
            "beforeinput:t d=\"か\" it=insertCompositionText c=false ic=true v=\"helloか\"",
            "input:t d=\"か\" it=insertCompositionText c=false ic=true v=\"helloか\"",
            "compositionend:t d=\"か\" v=\"helloか\"",
            "selectionchange:t v=\"helloか\""),
            Ask(session));
        Assert.Equal("helloか", session.FocusedTextField?.Value);
        Assert.Equal(6, session.FocusedTextField?.SelectionStart);
    }

    /// <summary>A composition replaces the selection it began on; a cancelled one puts the value back.</summary>
    [Fact]
    public void ACompositionReplacesTheSelectionAndACancelPutsItBack()
    {
        using var session = Start("t.focus(); t.setSelectionRange(1, 3);");
        Ask(session);

        session.DispatchComposition(new CompositionInput(CompositionInputKind.Start, string.Empty));
        session.DispatchComposition(new CompositionInput(CompositionInputKind.Commit, "Z"));
        Assert.Equal("hZlo", session.FocusedTextField?.Value);
        Assert.StartsWith("compositionstart:t d=\"el\" v=\"hello\";compositionupdate:t d=\"Z\"", Ask(session));

        session.DispatchComposition(new CompositionInput(CompositionInputKind.Update, "x"));
        session.DispatchComposition(new CompositionInput(CompositionInputKind.Cancel, string.Empty));
        Assert.Equal("hZlo", session.FocusedTextField?.Value);
        Assert.EndsWith("compositionupdate:t d=\"\" v=\"hZxlo\";" +
            "beforeinput:t d=\"\" it=insertCompositionText c=false ic=true v=\"hZxlo\";" +
            "input:t d=\"\" it=insertCompositionText c=false ic=true v=\"hZlo\";" +
            "compositionend:t d=\"\" v=\"hZlo\";selectionchange:t v=\"hZlo\"", Ask(session));
    }
}
