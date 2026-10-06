using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>:target</c>, <c>:user-valid</c> and <c>:user-invalid</c> match the element the page's fragment
/// names and the controls the user has interacted with, and a form is validated when it is submitted,
/// as Chromium does all three.
/// </summary>
/// <remarks>
/// <para>
/// <b>They matched nothing, and nothing validated a form</b>: a required field left empty was submitted,
/// <c>checkValidity()</c> read only a required control's <c>value</c> attribute, and
/// <c>requestSubmit()</c> did not exist.
/// </para>
/// <para>
/// What sets each state was measured in Chromium with trusted key presses and clicks: leaving a field the
/// user edited, a user's click on a checkbox, and any submission attempt -- which marks every control of
/// the form -- make a control interacted with; a script's value, <c>click()</c>, <c>checkValidity()</c>
/// or <c>change</c> event does not; <c>reset()</c> forgets it. <c>minlength</c> judges only a value the
/// user edited. A fragment navigation makes the element the fragment names the target, at once; an
/// element given the name later is not, and one removed is no longer. The layout is declared
/// (<see cref="DeclaredFrameLayoutView"/>).
/// </para>
/// </remarks>
public class ElementStateTests
{
    private const string PageUrl = "https://example.test/states";

    private static readonly Dictionary<string, System.Drawing.RectangleF> PageBoxes = new()
    {
        ["em"] = new(10, 10, 100, 20),
        ["mn"] = new(10, 40, 100, 20),
        ["cb"] = new(10, 70, 20, 20),
        ["go"] = new(10, 100, 40, 20),
        ["rs"] = new(60, 100, 40, 20),
    };

    private const string Form =
        "<form id=\"f\" action=\"/sent\"><input id=\"em\" name=\"em\" type=\"email\" required>" +
        "<input id=\"mn\" name=\"mn\" minlength=\"5\"><input id=\"cb\" name=\"cb\" type=\"checkbox\" required>" +
        "<button id=\"go\">go</button><button id=\"rs\" type=\"reset\">rs</button></form>" +
        "<section id=\"sec\">s</section><a name=\"named\">n</a>";

    private const string Recorder =
        "var pageOut = document.getElementById('out'), log = [];" +
        "function ids(selector) { return Array.prototype.map.call(document.querySelectorAll(selector), function (e) { return e.id || e.name || e.nodeName.toLowerCase(); }).join(',') || '-'; }" +
        "['invalid', 'submit', 'reset', 'change'].forEach(function (type) {" +
        "  window.addEventListener(type, function (e) {" +
        "    var s = type + ':' + e.target.id;" +
        "    if (type === 'submit') s += ' submitter=' + (e.submitter ? e.submitter.id : 'null') + ' trusted=' + e.isTrusted;" +
        "    log.push(s); }, true); });" +
        "function answer() {" +
        "  var a = 'ui=' + ids(':user-invalid') + ' uv=' + ids(':user-valid') + ' t=' + ids(':target') + ' ae=' + (document.activeElement.id || document.activeElement.nodeName.toLowerCase());" +
        "  if (log.length) a += ' | ' + log.join(';');" +
        "  log = []; return a; }" +
        "document.addEventListener('keyup', function (e) { if (e.key === 'F9') pageOut.textContent = answer(); }, true);";

    private static InteractiveSession Start(string script = "", string url = PageUrl, string form = Form)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredFrameLayoutView(PageBoxes),
        }));

        var session = engine.ExecuteInteractive(
            [Recorder + script], [],
            $"<html><body style=\"margin:0\">{form}<div id=\"out\"></div></body></html>",
            url);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    /// <summary>The page's answer, and what it recorded since it last answered.</summary>
    private static string Ask(InteractiveSession session)
    {
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, "F9", "F9") { KeyCode = 120 });
        return PageProbe.OutOf(session.CurrentHtml(), decode: true);
    }

    private static void Click(InteractiveSession session, double x, double y, out PointerInputResult result)
    {
        session.DispatchPointer(new PointerInput(PointerInputKind.Down, x, y) { Buttons = 1 });
        result = session.DispatchPointer(new PointerInput(PointerInputKind.Up, x, y));
    }

    private static void Click(InteractiveSession session, double x, double y) => Click(session, x, y, out _);

    private static void Type(InteractiveSession session, string text)
    {
        foreach (var character in text)
        {
            var key = character.ToString();
            session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, key, string.Empty) { KeyCode = char.ToUpperInvariant(character) });
            session.DispatchText(new TextInput(key));
            session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, key, string.Empty) { KeyCode = char.ToUpperInvariant(character) });
        }
    }

    private static void Key(InteractiveSession session, string key, int keyCode, bool shift = false)
    {
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, key, key) { KeyCode = keyCode, ShiftKey = shift });
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, key, key) { KeyCode = keyCode, ShiftKey = shift });
    }

    /// <summary>Has the page run what it runs on F8, which <paramref name="description"/> names for the reader.</summary>
    private static void Run(InteractiveSession session, string description)
    {
        _ = description;
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "F8", "F8") { KeyCode = 119 });
    }

    /// <summary>
    /// A field becomes <c>:user-invalid</c> when the user leaves it after editing it, not while they
    /// type; from then on it follows its validity as they type. A value too short for its
    /// <c>minlength</c> is invalid only while it is the user's.
    /// </summary>
    [Fact]
    public void AFieldIsJudgedOnlyOnceTheUserHasLeftIt()
    {
        using var session = Start(
            "document.addEventListener('keydown', function (e) {" +
            "  if (e.key === 'F8') { document.getElementById('mn').value = 'xy'; } }, true);");

        Click(session, 20, 20);
        Type(session, "a");
        Assert.Equal("ui=- uv=- t=- ae=em", Ask(session));

        Key(session, "Tab", 9);
        Assert.Equal("ui=em uv=- t=- ae=mn | change:em", Ask(session));

        Type(session, "abc");
        Assert.Equal("ui=em uv=- t=- ae=mn", Ask(session));

        Key(session, "Tab", 9, shift: true);
        Assert.Equal("ui=em,mn uv=- t=- ae=em | change:mn", Ask(session));

        Type(session, "@b.c");
        Assert.Equal("ui=mn uv=em t=- ae=em", Ask(session));

        // A script's value is not the user's: minlength no longer judges it.
        Run(session, "mn.value = 'xy'");
        Assert.Equal("ui=- uv=em,mn t=- ae=em", Ask(session));
    }

    /// <summary>
    /// A user's click on a checkbox makes it interacted with; a script's <c>click()</c>, a synthetic
    /// <c>change</c> and <c>checkValidity()</c> do not, and <c>reset()</c> forgets it.
    /// </summary>
    [Fact]
    public void OnlyTheUsersOwnChangesCount()
    {
        using var session = Start(
            "var f = document.getElementById('f');" +
            "document.addEventListener('keydown', function (e) {" +
            "  if (e.key === 'F8') { f.reset(); document.getElementById('cb').click(); document.getElementById('em').dispatchEvent(new Event('change')); f.checkValidity(); } }, true);");

        Click(session, 20, 80);
        Assert.Equal("ui=- uv=cb t=- ae=cb | change:cb", Ask(session));

        Run(session, "reset, a script's click, a synthetic change, checkValidity");
        Assert.StartsWith("ui=- uv=- t=- ae=cb |", Ask(session));
    }

    /// <summary>
    /// Enter in a field of an invalid form is a submission attempt: every control of the form is
    /// interacted with, each invalid one gets <c>invalid</c> in tree order, the first is focused, and
    /// nothing is submitted. Once it is valid, the user's click on its button submits it, with a
    /// trusted <c>submit</c> naming the button, and tells the host it was the page's to act on.
    /// </summary>
    [Fact]
    public void ASubmissionAttemptValidatesTheForm()
    {
        using var session = Start();

        Click(session, 20, 50);
        Key(session, "Enter", 13);
        Assert.Equal("ui=em,cb uv=mn t=- ae=em | invalid:em;invalid:cb", Ask(session));
        Assert.Null(session.TakePendingNavigation());

        Type(session, "a@b.c");
        Click(session, 20, 80);
        Click(session, 20, 110, out var result);

        Assert.Equal("ui=- uv=em,mn,cb t=- ae=go | change:em;change:cb;submit:f submitter=go trusted=true", Ask(session));
        Assert.True(result.Handled);
        Assert.False(result.DefaultPrevented);
        Assert.Equal(NavigationKind.FormSubmit, session.TakePendingNavigation()?.Kind);
    }

    /// <summary>A form with <c>novalidate</c>, or a submitter with <c>formnovalidate</c>, is submitted invalid.</summary>
    [Fact]
    public void NoValidateSubmitsAnInvalidForm()
    {
        using var session = Start(form: Form.Replace("<button id=\"go\">", "<button id=\"go\" formnovalidate>"));

        Click(session, 20, 110, out var result);

        Assert.Equal("ui=em,cb uv=mn t=- ae=go | submit:f submitter=go trusted=true", Ask(session));
        Assert.True(result.Handled);
        Assert.Equal(NavigationKind.FormSubmit, session.TakePendingNavigation()?.Kind);
    }

    /// <summary>
    /// <c>requestSubmit()</c> submits as a submit button would ask, validated and with a <c>submit</c>;
    /// a submitter that is not one of the form's submit buttons is refused.
    /// </summary>
    [Fact]
    public void RequestSubmitValidatesAndSubmits()
    {
        using var session = Start(
            "var f = document.getElementById('f');" +
            "document.addEventListener('keydown', function (e) {" +
            "  if (e.key !== 'F8') return;" +
            "  if (!window.second) { window.second = true; f.requestSubmit(); return; }" +
            "  var errors = [];" +
            "  try { f.requestSubmit(document.getElementById('em')); } catch (x) { errors.push(x.name); }" +
            "  var other = document.createElement('button'); document.body.appendChild(other);" +
            "  try { f.requestSubmit(other); } catch (x) { errors.push(x.name); }" +
            "  document.getElementById('em').value = 'a@b.c'; document.getElementById('cb').checked = true;" +
            "  f.requestSubmit(document.getElementById('go'));" +
            "  log.push(errors.join(',')); }, true);");

        Run(session, "requestSubmit()");
        Assert.Equal("ui=em,cb uv=mn t=- ae=em | invalid:em;invalid:cb", Ask(session));
        Assert.Null(session.TakePendingNavigation());

        Run(session, "requestSubmit(go)");
        Assert.Equal("ui=- uv=em,mn,cb t=- ae=em | submit:f submitter=go trusted=true;TypeError,NotFoundError", Ask(session));
        Assert.Equal(NavigationKind.FormSubmit, session.TakePendingNavigation()?.Kind);
    }

    /// <summary>
    /// <c>checkValidity()</c> judges what the user typed, with an <c>invalid</c> at each invalid control,
    /// and <c>reportValidity()</c> focuses the first.
    /// </summary>
    [Fact]
    public void CheckValidityJudgesTheLiveValue()
    {
        using var session = Start(
            "document.addEventListener('keydown', function (e) {" +
            "  if (e.key !== 'F8') return;" +
            "  var em = document.getElementById('em'), f = document.getElementById('f');" +
            "  log.push('em ' + em.checkValidity()); log.push('f ' + f.checkValidity()); log.push('report ' + f.reportValidity()); }, true);");

        Click(session, 20, 50);
        Click(session, 20, 20);
        Type(session, "x");
        Click(session, 20, 50);
        Run(session, "checks");

        Assert.Equal(
            "ui=em uv=- t=- ae=em | change:em;invalid:em;em false;invalid:em;invalid:cb;f false;invalid:em;invalid:cb;report false",
            Ask(session));
    }

    /// <summary>A user's click on a reset button resets its form, and tells the host it was the page's.</summary>
    [Fact]
    public void AResetButtonClickResetsTheForm()
    {
        using var session = Start();

        Click(session, 20, 20);
        Type(session, "x");
        Click(session, 20, 50);
        Click(session, 70, 110, out var result);

        Assert.Equal("ui=- uv=- t=- ae=rs | change:em;reset:f", Ask(session));
        Assert.True(result.Handled);
        Assert.Null(session.TakePendingNavigation());
    }

    /// <summary>
    /// The page's <c>:target</c> is the element its URL's fragment names, from the start; a fragment
    /// navigation moves it at once, <c>pushState</c> does not, and an element given the name after the
    /// navigation is not it.
    /// </summary>
    [Fact]
    public void TheTargetIsTheElementTheFragmentNames()
    {
        using var session = Start(
            "log.push('at load ' + ids(':target'));" +
            "document.addEventListener('keydown', function (e) {" +
            "  if (e.key !== 'F8') return;" +
            "  location.hash = '#named'; log.push(ids(':target'));" +
            "  history.pushState(null, '', '#sec'); log.push(ids(':target'));" +
            "  location.hash = '#late'; log.push(ids(':target'));" +
            "  var late = document.createElement('p'); late.id = 'late'; document.body.appendChild(late); log.push(ids(':target'));" +
            "  location.hash = '#late'; log.push(ids(':target'));" +
            "  late.remove(); log.push(ids(':target')); }, true);",
            url: PageUrl + "#sec");

        Assert.Equal("ui=- uv=- t=sec ae=body | at load sec", Ask(session));

        Run(session, "fragment navigations");
        Assert.Equal("ui=- uv=- t=- ae=body | named;named;-;-;late;-", Ask(session));
    }

    /// <summary>
    /// A host that follows a link into the page tells the page: its location moves, it hears
    /// <c>hashchange</c>, and the element the fragment names is its <c>:target</c>. A URL of another page
    /// is not a fragment of this one.
    /// </summary>
    [Fact]
    public void TheHostsFragmentNavigationIsThePages()
    {
        using var session = Start("window.addEventListener('hashchange', function () { log.push('hash ' + location.hash); });");

        Assert.True(session.NavigateToFragment(PageUrl + "#sec"));
        Assert.Equal("ui=- uv=- t=sec ae=body | hash #sec", Ask(session));

        Assert.False(session.NavigateToFragment("https://other.test/states#sec"));
        Assert.False(session.NavigateToFragment(PageUrl));
        Assert.Equal("ui=- uv=- t=sec ae=body", Ask(session));
    }

    /// <summary>
    /// The page the renderer is handed carries each element's state as <c>data-broiler-state</c>, in
    /// place of whatever the page wrote there; a script's own serialization carries none.
    /// </summary>
    [Fact]
    public void TheRenderedPageCarriesTheElementStates()
    {
        using var session = Start(
            "document.getElementById('mn').setAttribute('data-broiler-state', 'target user-interacted');" +
            "document.addEventListener('keydown', function (e) { if (e.key === 'F8') log.push(document.getElementById('em').outerHTML); }, true);",
            url: PageUrl + "#sec");

        Click(session, 20, 20);
        Type(session, "x");
        Click(session, 20, 80);
        var html = session.CurrentHtml();

        Assert.Contains("<section id=\"sec\" data-broiler-state=\"target\">", html);
        Assert.Contains("data-broiler-state=\"user-interacted user-edited\"", html);
        Assert.DoesNotContain("<input id=\"mn\" name=\"mn\" minlength=\"5\" data-broiler-state", html);

        Run(session, "outerHTML");
        Assert.DoesNotContain("data-broiler-state", Ask(session).Split(" | ")[1]);
    }
}
