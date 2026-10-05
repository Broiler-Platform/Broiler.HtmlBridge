using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>:hover</c>, <c>:active</c>, <c>:focus</c>, <c>:focus-visible</c> and <c>:focus-within</c> match
/// what the user is doing: in the page's own selector queries and computed style, and in the page the
/// renderer is handed, whose elements carry their state as <c>data-broiler-user-action</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>They matched nothing</b>, whatever the user did: a page's <c>a:hover</c> rule never applied and
/// <c>querySelector(':hover')</c> found nothing.
/// </para>
/// <para>
/// What matches when was measured in Chromium: hover is the element under the pointer and its
/// ancestors; a frame's element is hovered in its page while the pointer is over the frame, but
/// focus inside a frame makes its element neither <c>:focus</c> nor its ancestors
/// <c>:focus-within</c>; a button the mouse focused is <c>:focus</c> but not <c>:focus-visible</c>,
/// a text field is both, and anything the keyboard focused is both. The layout is declared
/// (<see cref="DeclaredFrameLayoutView"/>).
/// </para>
/// </remarks>
public class UserActionStyleTests
{
    private const string PageUrl = "https://example.test/states";

    private static readonly Dictionary<string, System.Drawing.RectangleF> PageBoxes = new()
    {
        ["outer"] = new(10, 10, 200, 200),
        ["inner"] = new(50, 50, 50, 50),
        ["field"] = new(10, 220, 150, 20),
        ["btn"] = new(10, 250, 40, 20),
        ["frame"] = new(300, 10, 300, 200),
        ["fin"] = new(310, 20, 100, 20),
    };

    private const string Body =
        "<div id=\"outer\"><div id=\"inner\"></div></div><input id=\"field\"><button id=\"btn\">B</button>" +
        "<iframe id=\"frame\" srcdoc=\"<input id='fin'><script>document.addEventListener('keyup', function (e) { if (e.key === 'F9') parent.respond(); }, true);</script>\"></iframe>";

    private static InteractiveSession Start(string style = "", string script = "")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredFrameLayoutView(PageBoxes),
        }));

        var session = engine.ExecuteInteractive(
            ["var pageOut = document.getElementById('out'), pageDoc = document; document.getElementById('frame').contentWindow;" +
             "function ids(selector, doc) { return Array.prototype.map.call((doc || pageDoc).querySelectorAll(selector), function (e) { return e.id || e.nodeName.toLowerCase(); }).join(','); }" +
             script], [],
            $"<html><head><style>{style}</style></head><body style=\"margin:0\">{Body}<div id=\"out\"></div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    /// <summary>
    /// Has the page's script write its <c>answer()</c> into <c>#out</c> and reads it: the page listens
    /// for F9, and the frame passes it on when focus is in the frame. <paramref name="step"/> names the
    /// moment, for a failure's message.
    /// </summary>
    private static string Ask(InteractiveSession session, string step)
    {
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, "F9", "F9") { KeyCode = 120 });
        return PageProbe.OutOf(session.CurrentHtml(), decode: true);
    }

    private const string Answer =
        "window.respond = function () { pageOut.textContent = answer(); };" +
        "pageDoc.addEventListener('keyup', function (e) { if (e.key === 'F9') respond(); }, true);";

    private static void Move(InteractiveSession session, double x, double y, int buttons = 0) =>
        session.DispatchPointer(new PointerInput(PointerInputKind.Move, x, y) { Buttons = buttons });

    private static void Press(InteractiveSession session, double x, double y)
    {
        session.DispatchPointer(new PointerInput(PointerInputKind.Down, x, y) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, x, y));
    }

    /// <summary>
    /// <c>:hover</c> is the element under the pointer and every ancestor, in the page's own queries and
    /// its computed style; the frame's element while the pointer is over the frame.
    /// </summary>
    [Fact]
    public void HoverFollowsThePointer()
    {
        using var session = Start(
            "#inner:hover { color: rgb(255, 0, 0) }",
            "function answer() { return ids(':hover') + ' ' + getComputedStyle(pageDoc.getElementById('inner')).color; }" + Answer);

        Move(session, 60, 60);
        Assert.Equal("html,body,outer,inner rgb(255, 0, 0)", Ask(session, "hover"));

        Move(session, 20, 20);
        Assert.Equal("html,body,outer rgb(0, 0, 0)", Ask(session, "hover"));

        Move(session, 320, 30);
        Assert.Equal("html,body,frame rgb(0, 0, 0)", Ask(session, "hover"));
    }

    /// <summary>
    /// The page the renderer is handed carries each hovered element's state, and none for the rest, in
    /// place of whatever the page wrote there itself.
    /// </summary>
    [Fact]
    public void TheRenderedPageCarriesTheHoverState()
    {
        using var session = Start(script: "document.getElementById('field').setAttribute('data-broiler-user-action', 'hover focus');");

        Move(session, 60, 60);
        var html = session.CurrentHtml();

        Assert.Contains("<div id=\"inner\" data-broiler-user-action=\"hover\">", html);
        Assert.Contains("<div id=\"outer\" data-broiler-user-action=\"hover\">", html);
        Assert.DoesNotContain("<input id=\"field\" data-broiler-user-action", html);
    }

    /// <summary>
    /// A field the mouse focused is <c>:focus</c> and <c>:focus-visible</c>, and its ancestors
    /// <c>:focus-within</c>; a button the mouse focused is <c>:focus</c> alone; the keyboard's focus is
    /// visible on anything; and the renderer is handed the same.
    /// </summary>
    [Fact]
    public void FocusIsVisibleOnAFieldAndWhereTheKeyboardMovedIt()
    {
        using var session = Start(
            script: "function answer() { return [ids(':focus'), ids(':focus-visible'), ids(':focus-within')].join(' '); }" + Answer);

        Press(session, 20, 225);
        Assert.Equal("field field html,body,field", Ask(session, "field"));
        Assert.Contains("<input id=\"field\" data-broiler-user-action=\"hover focus focus-visible focus-within\">", session.CurrentHtml());

        Press(session, 20, 255);
        Assert.Equal("btn  html,body,btn", Ask(session, "button"));

        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "Tab", "Tab") { KeyCode = 9, ShiftKey = true });
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, "Tab", "Tab") { KeyCode = 9, ShiftKey = true });
        Assert.Equal("field field html,body,field", Ask(session, "tabbed back"));

        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "Tab", "Tab") { KeyCode = 9 });
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, "Tab", "Tab") { KeyCode = 9 });
        Assert.Equal("btn btn html,body,btn", Ask(session, "tabbed"));
    }

    /// <summary>
    /// Focus inside a frame: the frame's field is <c>:focus</c> in its document, and the frame's
    /// element in the page matches neither <c>:focus</c> nor <c>:focus-within</c>, and neither do its
    /// ancestors.
    /// </summary>
    [Fact]
    public void FocusInAFrameDoesNotReachThePagesSelectors()
    {
        using var session = Start(
            script: "function answer() { var frameDoc = pageDoc.getElementById('frame').contentDocument;" +
                    " return [ids(':focus'), ids(':focus-within'), ids(':focus', frameDoc), ids(':focus-within', frameDoc)].join(' '); }" + Answer);

        Press(session, 320, 30);

        Assert.Equal("  fin html,body,fin", Ask(session, "frame"));
    }

    /// <summary><c>:active</c> is the pressed element and its ancestors, from the press until the release.</summary>
    [Fact]
    public void ActiveIsThePressedElementWhileTheButtonIsHeld()
    {
        using var session = Start(
            script: "function answer() { return ids(':active'); }" + Answer);

        session.DispatchPointer(new PointerInput(PointerInputKind.Down, 60, 60) { Buttons = 1 });
        Assert.Equal("html,body,outer,inner", Ask(session, "held"));

        session.DispatchPointer(new PointerInput(PointerInputKind.Up, 60, 60));
        Assert.Equal(string.Empty, Ask(session, "released"));
    }

    /// <summary>
    /// A move that changes only what is hovered changes what the page renders when its style sheets
    /// mention <c>:hover</c>, and not otherwise, so a host shows such a page again only when it must.
    /// </summary>
    [Theory]
    [InlineData("#inner:hover { color: red }", true)]
    [InlineData("#inner { color: red }", false)]
    public void HoverChangesTheRenderVersionOnlyWhereARuleMentionsIt(string style, bool changes)
    {
        using var session = Start(style);
        Move(session, 20, 20);
        var before = session.RenderVersion;

        Move(session, 60, 60);

        Assert.Equal(changes, session.RenderVersion != before);
    }

    /// <summary>
    /// A frame's document, handed to the renderer inside its element's markup, carries its hovered
    /// elements' state too.
    /// </summary>
    [Fact]
    public void AFramesRenderedDocumentCarriesItsHoverState()
    {
        using var session = Start();

        Move(session, 320, 30);
        var html = session.CurrentHtml();

        Assert.Contains("id=&quot;fin&quot; data-broiler-user-action=&quot;hover&quot;", html);
    }
}
