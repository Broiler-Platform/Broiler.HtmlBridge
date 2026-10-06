using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A script's <c>click()</c>, a <c>MouseEvent</c> click it dispatches, <c>form.reset()</c>,
/// <c>form.submit()</c> and <c>requestSubmit()</c> do what they do in Chromium.
/// </summary>
/// <remarks>
/// <para>
/// <b>They did not.</b> <c>click()</c> toggled a checkbox without its <c>input</c> or <c>change</c>, clicked
/// disabled controls, and fired an untrusted, unvalidated <c>submit</c> of its own for a submit button; a
/// reset button, a label and a link did nothing, and neither did a dispatched <c>MouseEvent</c> click.
/// <c>form.reset()</c> fired no <c>reset</c>, and <c>form.submit()</c> fired a <c>submit</c> whose
/// cancelling listener stopped the submission.
/// </para>
/// <para>
/// Every expectation was measured in Chromium (the built-in browser, scripts on an injected form): the
/// click is untrusted, a checkbox reads changed in its listeners and gets a trusted <c>input</c> and
/// <c>change</c> after, a disabled control -- by its own attribute or a fieldset's, except in the first
/// legend -- is not clicked, a submit button validates and submits, <c>submit()</c> fires no
/// <c>submit</c>, a nested <c>reset()</c> or <c>requestSubmit()</c> is ignored, and the microtasks a
/// script queued before any of these run after it.
/// </para>
/// </remarks>
public class ScriptActivationTests
{
    private const string PageUrl = "https://example.test/activation";

    private const string Body =
        "<form id=\"f\" action=\"/sent\"><input id=\"q\" name=\"q\" required><input id=\"cb\" name=\"cb\" type=\"checkbox\">" +
        "<input id=\"ra\" name=\"r\" type=\"radio\" checked><input id=\"rb\" name=\"r\" type=\"radio\">" +
        "<button id=\"go\">go</button><button id=\"rs\" type=\"reset\">rs</button><button id=\"bt\" type=\"button\">bt</button>" +
        "<button id=\"dis\" type=\"button\" disabled><span id=\"inside\">x</span></button>" +
        "<fieldset disabled><legend><button id=\"first\" type=\"button\">1</button></legend><legend><button id=\"second\" type=\"button\">2</button></legend>" +
        "<div><button id=\"infs\" type=\"button\">fs</button></div></fieldset></form>" +
        "<label id=\"lfor\" for=\"cb2\">for</label><input id=\"cb2\" type=\"checkbox\"><label id=\"lwrap\">wrap <input id=\"cb3\" type=\"checkbox\"></label>" +
        "<a id=\"frag\" href=\"#part\">frag</a><a id=\"away\" href=\"/elsewhere\">away</a><a id=\"blank\" href=\"/popup\" target=\"_blank\">blank</a>" +
        "<a id=\"dl\" href=\"/file.zip\" download>dl</a><a id=\"js\" href=\"javascript:void(0)\">js</a><p id=\"part\">part</p>" +
        "<div id=\"out\"></div>";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function $(id) { return document.getElementById(id); }" +
        "function rec(e) { log.push(e.type + '@' + (e.target.id || e.target.nodeName) + (e.isTrusted ? '' : ' untrusted') +" +
        "  (e.type === 'submit' ? ' by ' + (e.submitter ? e.submitter.id : 'null') : '')); }" +
        "['click', 'input', 'change', 'submit', 'reset', 'invalid'].forEach(function (t) { window.addEventListener(t, rec, true); });";

    private static (string Out, NavigationRequest? Pending) Run(string script)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        using var session = engine.ExecuteInteractive([Recorder + script + ";show();"], [], $"<html><body>{Body}</body></html>", PageUrl);
        Assert.NotNull(session);
        var html = session!.SettleLoadWindow();
        return (PageProbe.OutOf(html, decode: true), session.TakePendingNavigation());
    }

    /// <summary>A checkbox a script clicks reads changed in its listeners, then gets a trusted <c>input</c> and <c>change</c>.</summary>
    [Fact]
    public void ACheckboxAScriptClicksChangesWithItsEvents()
    {
        Assert.Equal(
            "click@cb untrusted|listener true MouseEvent -1 '' 0 true true|input@cb|change@cb|checked true",
            Run("$('cb').addEventListener('click', function (e) {" +
                "  log.push(['listener', $('cb').checked, e instanceof MouseEvent ? 'MouseEvent' : 'other', e.pointerId, \"'\" + e.pointerType + \"'\", e.detail, e.composed, e.cancelable].join(' ')); });" +
                "$('cb').click(); log.push('checked ' + $('cb').checked)").Out);
    }

    /// <summary>A cancelled click changes the checkbox back, and fires neither <c>input</c> nor <c>change</c>.</summary>
    [Fact]
    public void ACancelledClickChangesNothing()
    {
        Assert.Equal(
            "click@cb untrusted|listener true|checked false",
            Run("$('cb').addEventListener('click', function (e) { e.preventDefault(); log.push('listener ' + $('cb').checked); });" +
                "$('cb').click(); log.push('checked ' + $('cb').checked)").Out);
    }

    /// <summary>
    /// A disabled control is not clicked -- by its own <c>disabled</c>, or by a disabled fieldset's except
    /// in its first legend -- while an element inside a disabled button is, and nothing comes of it.
    /// </summary>
    [Fact]
    public void ADisabledControlIsNotClicked()
    {
        Assert.Equal(
            "click@first untrusted|click@inside untrusted|dis heard inside",
            Run("$('dis').addEventListener('click', function (e) { log.push('dis heard ' + e.target.id); });" +
                "['dis', 'first', 'second', 'infs', 'inside'].forEach(function (id) { $(id).click(); })").Out);
    }

    /// <summary>A checked radio button clicked again fires only its click; another of the group changes, and the first is unchecked.</summary>
    [Fact]
    public void ARadioButtonChangesOnlyWhenItWasNotChecked()
    {
        Assert.Equal(
            "click@ra untrusted|click@rb untrusted|input@rb|change@rb|ra false rb true",
            Run("$('ra').click(); $('rb').click(); log.push('ra ' + $('ra').checked + ' rb ' + $('rb').checked)").Out);
    }

    /// <summary>A label a script clicks clicks its control, which changes: the one it names, or the one inside it.</summary>
    [Fact]
    public void ALabelClicksItsControl()
    {
        Assert.Equal(
            "click@lfor untrusted|click@cb2 untrusted|input@cb2|change@cb2|click@lwrap untrusted|click@cb3 untrusted|input@cb3|change@cb3|true true",
            Run("$('lfor').click(); $('lwrap').click(); log.push($('cb2').checked + ' ' + $('cb3').checked)").Out);
    }

    /// <summary>A <c>click()</c> on an element whose <c>click()</c> is running does nothing.</summary>
    [Fact]
    public void AClickInsideItsOwnClickIsIgnored()
    {
        Assert.Equal(
            "click@cb untrusted|inner|input@cb|change@cb|checked true",
            Run("var inner = 0; $('cb').addEventListener('click', function () { if (inner++ < 2) { log.push('inner'); $('cb').click(); } });" +
                "$('cb').click(); log.push('checked ' + $('cb').checked)").Out);
    }

    /// <summary>A checkbox in no document changes when clicked, and fires no <c>input</c> or <c>change</c>.</summary>
    [Fact]
    public void ACheckboxInNoDocumentChangesSilently()
    {
        Assert.Equal(
            "click untrusted|checked true",
            Run("var d = document.createElement('input'); d.type = 'checkbox';" +
                "['click', 'input', 'change'].forEach(function (t) { d.addEventListener(t, function (e) { log.push(t + (e.isTrusted ? '' : ' untrusted')); }); });" +
                "d.click(); log.push('checked ' + d.checked)").Out);
    }

    /// <summary>
    /// A submit button a script clicks submits as a user's click would: an invalid form is held back with
    /// its <c>invalid</c>, its first invalid control focused and every control <c>:user-invalid</c> or
    /// <c>:user-valid</c>; a valid one gets a trusted <c>submit</c> naming the button, and goes.
    /// </summary>
    [Fact]
    public void ASubmitButtonAScriptClicksSubmitsValidated()
    {
        var (held, heldPending) = Run(
            "$('go').click(); log.push(document.activeElement.id + ' ' + $('q').matches(':user-invalid'))");
        Assert.Equal("click@go untrusted|invalid@q|q true", held);
        Assert.Null(heldPending);

        var (sent, pending) = Run("$('q').value = 'x'; $('go').click()");
        Assert.Equal("click@go untrusted|submit@f by go", sent);
        Assert.Equal(NavigationKind.FormSubmit, pending?.Kind);
    }

    /// <summary>A reset button a script clicks resets its form, with the form's trusted <c>reset</c>.</summary>
    [Fact]
    public void AResetButtonAScriptClicksResetsTheForm()
    {
        Assert.Equal(
            "click@rs untrusted|reset@f|'' false",
            Run("$('q').value = 'typed'; $('cb').checked = true; $('rs').click(); log.push(\"'\" + $('q').value + \"' \" + $('cb').checked)").Out);
    }

    /// <summary>
    /// A <c>MouseEvent</c> named <c>click</c> a script dispatches activates as a click does; an <c>Event</c>
    /// of that name runs its listeners and nothing else, and a <c>mousedown</c> activates nothing.
    /// </summary>
    [Fact]
    public void ADispatchedMouseEventClickActivates()
    {
        Assert.Equal(
            "click@cb untrusted|input@cb|change@cb|true|click@cb untrusted|true|true|click@go untrusted|invalid@q",
            Run("log.push(String($('cb').dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))));" +
                "$('cb').dispatchEvent(new Event('click', { bubbles: true })); log.push(String($('cb').checked));" +
                "$('cb').dispatchEvent(new MouseEvent('mousedown', { bubbles: true })); log.push(String($('cb').checked));" +
                "$('go').dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true }))").Out);
    }

    /// <summary>
    /// A link a script clicks is followed: into the page at once, elsewhere through the host. One that
    /// opens another window, a download, or a <c>javascript:</c> URL goes nowhere.
    /// </summary>
    [Fact]
    public void ALinkAScriptClicksIsFollowed()
    {
        var (fragment, fragmentPending) = Run("$('frag').click(); log.push(location.hash)");
        Assert.Equal("click@frag untrusted|#part", fragment);
        Assert.Null(fragmentPending);

        var (away, pending) = Run("$('away').click()");
        Assert.Equal("click@away untrusted", away);
        Assert.Equal("https://example.test/elsewhere", pending?.Url);

        var (nowhere, nowherePending) = Run("$('blank').click(); $('dl').click(); $('js').click()");
        Assert.Equal("click@blank untrusted|click@dl untrusted|click@js untrusted", nowhere);
        Assert.Null(nowherePending);
    }

    /// <summary>
    /// <c>form.submit()</c> fires no <c>submit</c> and validates nothing, so a listener that would have
    /// cancelled it does not stop it; <c>requestSubmit()</c> fires it, and that listener does.
    /// </summary>
    [Fact]
    public void SubmitFiresNoSubmitEvent()
    {
        var (submitted, pending) = Run(
            "$('f').addEventListener('submit', function (e) { e.preventDefault(); }); $('f').submit(); log.push('q ' + $('q').matches(':user-invalid'))");
        Assert.Equal("q false", submitted);
        Assert.Equal(NavigationKind.FormSubmit, pending?.Kind);

        var (requested, requestedPending) = Run(
            "$('f').addEventListener('submit', function (e) { e.preventDefault(); }); $('q').value = 'x'; $('f').requestSubmit()");
        Assert.Equal("submit@f by null", requested);
        Assert.Null(requestedPending);
    }

    /// <summary>
    /// A submission asked for from the form's own <c>submit</c> listener is ignored, while a
    /// <c>submit()</c> there goes ahead.
    /// </summary>
    [Fact]
    public void ARequestSubmitInsideItsOwnSubmitIsIgnored()
    {
        var (ignored, pending) = Run(
            "$('q').value = 'x'; var n = 0;" +
            "$('f').addEventListener('submit', function (e) { e.preventDefault(); if (n++ === 0) { $('f').requestSubmit(); $('go').click(); log.push('inner done'); } });" +
            "$('f').requestSubmit(); log.push('n ' + n)");
        Assert.Equal("submit@f by null|click@go untrusted|inner done|n 1", ignored);
        Assert.Null(pending);

        var (sent, sentPending) = Run(
            "$('q').value = 'x'; $('f').addEventListener('submit', function (e) { e.preventDefault(); $('f').submit(); }); $('f').requestSubmit()");
        Assert.Equal("submit@f by null", sent);
        Assert.Equal(NavigationKind.FormSubmit, sentPending?.Kind);
    }

    /// <summary>
    /// <c>form.reset()</c> fires the form's trusted, cancelable <c>reset</c>, whose listener still sees the
    /// old values; a cancelled one resets nothing, and a <c>reset()</c> inside it is ignored.
    /// </summary>
    [Fact]
    public void ResetFiresAResetEventOnce()
    {
        Assert.Equal(
            "reset@f|listener 'typed' bubbles true cancelable true|value ''|reset@f|listener 'again' bubbles true cancelable true|value 'again'|reset@f|nested|value ''",
            Run("var mode = 'plain';" +
                "function value() { log.push(\"value '\" + $('q').value + \"'\"); }" +
                "$('f').addEventListener('reset', function (e) {" +
                "  log.push(\"listener '\" + $('q').value + \"' bubbles \" + e.bubbles + ' cancelable ' + e.cancelable);" +
                "  if (mode === 'cancel') e.preventDefault();" +
                "  if (mode === 'nested') { log.pop(); log.push('nested'); mode = 'done'; $('f').reset(); } });" +
                "$('q').value = 'typed'; $('f').reset(); value();" +
                "mode = 'cancel'; $('q').value = 'again'; $('f').reset(); value();" +
                "mode = 'nested'; $('f').reset(); value()").Out);
    }

    /// <summary>
    /// The microtasks a script queued before it called <c>click()</c>, <c>reset()</c>,
    /// <c>requestSubmit()</c> or <c>checkValidity()</c> -- a promise's and a <c>queueMicrotask</c>
    /// callback -- run after that script, in the order queued, not inside the call.
    /// </summary>
    [Fact]
    public void AScriptsCallsRunNoMicrotasks()
    {
        Assert.Equal(
            "click@cb untrusted|input@cb|change@cb|reset@f|invalid@q|invalid@q|after|promise|queued",
            Run("Promise.resolve().then(function () { log.push('promise'); show(); });" +
                "queueMicrotask(function () { log.push('queued'); show(); });" +
                "$('cb').click(); $('f').reset(); $('f').requestSubmit(); $('f').checkValidity(); log.push('after')").Out);
    }

    /// <summary>The event constructors make instances of themselves, as <c>document.createEvent</c> does of the interface it names.</summary>
    [Fact]
    public void AnEventIsAnInstanceOfItsInterface()
    {
        Assert.Equal(
            "true true true true true false",
            Run("log.push([new MouseEvent('click') instanceof MouseEvent, new Event('x') instanceof Event, new CustomEvent('x') instanceof CustomEvent," +
                " document.createEvent('MouseEvents') instanceof MouseEvent, document.createEvent('HTMLEvents').constructor === Event," +
                " new Event('x') instanceof MouseEvent].join(' '))").Out);
    }
}
