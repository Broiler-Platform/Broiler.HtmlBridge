using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A form's entry list is constructed as Chromium constructs it: with its <c>formdata</c> event, its
/// submitter's entries, and what the event's listeners change reaching the submission.
/// </summary>
/// <remarks>
/// <para>
/// <b>No <c>formdata</c> fired</b>, a submission named neither its submitter nor the submitter's
/// <c>formaction</c>, so a form with two buttons could not tell its server which was used, a
/// <c>method="dialog"</c> form navigated instead of closing its dialog, and a form aimed at a frame or a new
/// window navigated the page.
/// </para>
/// <para>
/// Measured in Chromium: <c>formdata</c> (a trusted <c>FormDataEvent</c>, bubbling, not cancelable) follows
/// <c>submit</c>, and fires for <c>form.submit()</c> and <c>new FormData(form)</c> too; its <c>formData</c>
/// holds the submitter's entry in tree order -- a button's value or nothing, a submit input's value or its
/// label, an image button's <c>name.x</c> and <c>name.y</c>; what its listeners set, append and delete is
/// submitted; inside it <c>new FormData(form)</c> throws <c>InvalidStateError</c> and <c>form.submit()</c>
/// does nothing.
/// </para>
/// </remarks>
public class FormDataEventTests
{
    private const string PageUrl = "https://example.test/formdata";

    private const string Body =
        "<form id=\"f\" action=\"/act\"><input name=\"q\" value=\"v\"><input type=\"checkbox\" name=\"c\" checked>" +
        "<button id=\"b1\" name=\"btn\" value=\"bv\">b1</button><button id=\"b2\" name=\"b2\" formaction=\"/other\" formmethod=\"post\">b2</button>" +
        "<input id=\"s1\" type=\"submit\" name=\"s\"><input id=\"im\" type=\"image\" name=\"im\" alt=\"i\"></form>" +
        "<dialog id=\"d\" open><form id=\"df\" method=\"dialog\"><button id=\"ok\" value=\"yes\">ok</button></form></dialog>" +
        "<iframe name=\"sink\" srcdoc=\"&lt;p&gt;sink&lt;/p&gt;\"></iframe>" +
        "<form id=\"tf\" action=\"/framed\" target=\"sink\"><input name=\"t\" value=\"1\"></form>" +
        "<form id=\"bf\" action=\"/blank\" target=\"_blank\"><input name=\"t\" value=\"1\"></form>" +
        "<div id=\"out\"></div>";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function $(id) { return document.getElementById(id); }" +
        "function entries(fd) { var all = []; fd.forEach(function (value, name) { all.push(name + '=' + value); }); return all.join('&'); }";

    private static (string Out, NavigationRequest? Pending) Run(string script)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        using var session = engine.ExecuteInteractive([Recorder + script + ";show();"], [], $"<html><body>{Body}</body></html>", PageUrl);
        Assert.NotNull(session);
        var html = session!.SettleLoadWindow();
        return (PageProbe.OutOf(html, decode: true), session.TakePendingNavigation());
    }

    /// <summary>
    /// <c>formdata</c> follows <c>submit</c>, a trusted, bubbling <c>FormDataEvent</c> whose <c>formData</c>
    /// holds the submitter's entry; what its listeners do is what the host is told to submit, as which
    /// button, to where.
    /// </summary>
    [Fact]
    public void AFormdataListenersChangesTravelWithTheSubmission()
    {
        var (log, pending) = Run(
            "$('f').addEventListener('submit', function () { log.push('submit'); });" +
            "document.addEventListener('formdata', function (e) {" +
            "  log.push(['formdata', e.isTrusted, e instanceof FormDataEvent, e.bubbles, e.cancelable, e.composed, entries(e.formData)].join(' '));" +
            "  e.formData.set('q', 'changed'); e.formData.append('added', 'yes'); e.formData.delete('c');" +
            "  window.kept = e.formData; });" +
            "$('f').requestSubmit($('b1')); kept.append('late', 'no')");

        Assert.Equal("submit|formdata true true true false false q=v&c=on&btn=bv", log);
        Assert.Equal(NavigationKind.FormSubmit, pending?.Kind);
        Assert.Equal("https://example.test/act", pending!.Url);
        Assert.Equal(2, pending.SubmitterIndex);
        Assert.Equal(
            [new FormDataEdit(FormDataEditKind.Set, "q", "changed"), new FormDataEdit(FormDataEditKind.Append, "added", "yes"), new FormDataEdit(FormDataEditKind.Delete, "c")],
            pending.FormDataEdits);
    }

    /// <summary>
    /// A submitter's entries, in tree order: a button's value, a submit input's value or its label, an image
    /// button's <c>name.x</c> and <c>name.y</c>; its <c>formaction</c> is the submission's.
    /// </summary>
    [Fact]
    public void TheSubmittersEntriesAndActionAreItsOwn()
    {
        var (log, pending) = Run(
            "log.push(entries(new FormData($('f'), $('s1'))));" +
            "log.push(entries(new FormData($('f'), $('im'))));" +
            "log.push(entries(new FormData($('f'))));" +
            "$('f').requestSubmit($('b2'))");

        Assert.Equal("q=v&c=on&s=Submit|q=v&c=on&im.x=0&im.y=0|q=v&c=on", log);
        Assert.Equal("https://example.test/other", pending?.Url);
        Assert.Equal(3, pending!.SubmitterIndex);
    }

    /// <summary>
    /// <c>form.submit()</c> fires <c>formdata</c> but not <c>submit</c>, and <c>new FormData(form)</c> fires it
    /// too; inside it <c>new FormData(form)</c> throws and a submission of the form does nothing.
    /// </summary>
    [Fact]
    public void TheEntryListCannotBeConstructedTwiceAtOnce()
    {
        var (log, pending) = Run(
            "$('f').addEventListener('submit', function () { log.push('submit'); });" +
            "$('f').addEventListener('formdata', function (e) {" +
            "  try { new FormData($('f')); log.push('nested ok'); } catch (err) { log.push('nested ' + err.name); }" +
            "  $('f').submit(); $('f').requestSubmit(); log.push('formdata ' + entries(e.formData)); });" +
            "var fd = new FormData($('f')); log.push('constructed ' + entries(fd));" +
            "$('f').submit()");

        Assert.Equal("nested InvalidStateError|formdata q=v&c=on|constructed q=v&c=on|nested InvalidStateError|formdata q=v&c=on", log);
        Assert.Equal(NavigationKind.FormSubmit, pending?.Kind);
        Assert.Equal(-1, pending!.SubmitterIndex);
    }

    /// <summary>A <c>method="dialog"</c> form closes its dialog with the submitter's value, and navigates nothing.</summary>
    [Fact]
    public void ADialogFormClosesItsDialog()
    {
        var (log, pending) = Run(
            "$('df').addEventListener('formdata', function () { log.push('formdata'); });" +
            "$('ok').click(); log.push('open ' + $('d').open + ' returnValue ' + $('d').returnValue)");

        Assert.Equal("formdata|open false returnValue yes", log);
        Assert.Null(pending);
    }

    /// <summary>
    /// A submission into a frame is not one the host can perform, and a script's into a new window is
    /// stopped as a pop-up: neither navigates the page.
    /// </summary>
    [Fact]
    public void ASubmissionIntoAnotherBrowsingContextLeavesThePageAlone()
    {
        var (_, framed) = Run("$('tf').submit()");
        Assert.Null(framed);

        var (_, blank) = Run("$('bf').submit()");
        Assert.Null(blank);
    }

    /// <summary>A <c>FormData</c> is iterable: <c>entries()</c>, <c>keys()</c>, <c>values()</c> and <c>for…of</c>.</summary>
    [Fact]
    public void AFormDataIsIterable()
    {
        var (log, _) = Run(
            "var fd = new FormData($('f')); var seen = [];" +
            "for (var pair of fd) seen.push(pair.join('='));" +
            "log.push(seen.join('&'), Array.from(fd.keys()).join(','), Array.from(fd.values()).join(','), JSON.stringify(Object.fromEntries(fd.entries())))");

        Assert.Equal("q=v&c=on|q,c|v,on|{\"q\":\"v\",\"c\":\"on\"}", log);
    }
}
