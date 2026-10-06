using System.Text;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The files a user chose in the host's picker are the page's: <c>input.files</c> lists them, <c>value</c>
/// names the first, a <c>FormData</c> holds the same objects, and the choice fires <c>input</c> and
/// <c>change</c> -- or a closed picker <c>cancel</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The files stayed the host's.</b> The window read them from the disk for its own submission, and the
/// page saw an empty <c>FileList</c>, an empty value, no events, and an empty file in its <c>formdata</c>.
/// </para>
/// <para>
/// Measured in Chromium, the events from HTML's "update the file selection"
/// and Chromium's <c>FileInputType</c>, since a picker cannot be driven from a script.
/// </para>
/// </remarks>
public class FileInputTests
{
    private const string PageUrl = "https://example.test/files";

    private static readonly DateTimeOffset Modified = DateTimeOffset.FromUnixTimeMilliseconds(1700000000000);

    private const string Recorder =
        "var out = document.getElementById('out'), log = [], one = document.getElementById('one'), many = document.getElementById('many');" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }" +
        "function files(i) { return Array.from(i.files).map(function (f) { return f.name + '/' + f.type + '/' + f.size + '/' + f.lastModified; }).join(','); }";

    private static InteractiveSession Start(string script)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        var session = engine.ExecuteInteractive(
            [Recorder + script + ";show();"], [],
            "<html><body><form id=\"f\"><input type=\"file\" id=\"one\" name=\"one\"><input type=\"file\" id=\"many\" name=\"many\" multiple>" +
            "<input type=\"file\" id=\"off\" name=\"off\" disabled></form><div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    private static ChosenFile File(string name, string type, string content) =>
        new(name, type, Modified, Encoding.UTF8.GetBytes(content));

    /// <summary>
    /// The choice fires <c>input</c> -- trusted, bubbling, composed -- and <c>change</c>, with <c>files</c>
    /// and <c>value</c> already the new ones; the same choice again, or one for a disabled input, fires nothing.
    /// </summary>
    [Fact]
    public void TheUsersChoiceIsThePagesFiles()
    {
        using var session = Start(
            "[one, many].forEach(function (i) { ['input', 'change'].forEach(function (t) { i.addEventListener(t, function (e) {" +
            "  note(t + ' ' + i.id + ' ' + e.isTrusted + ' ' + e.bubbles + ' ' + e.composed + ' ' + e.constructor.name + ' ' + files(i) + ' ' + i.value); }); }); });" +
            "note(files(one) + ' ' + JSON.stringify(one.value) + ' ' + (one.files === one.files) + ' ' + Object.prototype.toString.call(one.files));");
        session.SettleLoadWindow();

        Assert.True(session.SetFilesByUser(0, [File("a.txt", "text/plain", "hello")]));
        Assert.False(session.SetFilesByUser(0, [File("a.txt", "text/plain", "hello")]));
        Assert.True(session.SetFilesByUser(1, [File("b.bin", "", "x"), File("c d.png", "image/png", "yy")]));
        Assert.False(session.SetFilesByUser(2, [File("e.txt", "text/plain", "e")]));
        Assert.False(session.SetFilesByUser(3, [File("f.txt", "text/plain", "f")]));

        Assert.Equal(
            " \"\" true [object FileList]|" +
            "input one true true true Event a.txt/text/plain/5/1700000000000 C:\\fakepath\\a.txt|" +
            "change one true true false Event a.txt/text/plain/5/1700000000000 C:\\fakepath\\a.txt|" +
            "input many true true true Event b.bin//1/1700000000000,c d.png/image/png/2/1700000000000 C:\\fakepath\\b.bin|" +
            "change many true true false Event b.bin//1/1700000000000,c d.png/image/png/2/1700000000000 C:\\fakepath\\b.bin",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A <c>FormData</c> of the form holds the input's own <c>File</c> objects, whose bytes a script reads;
    /// an input with nothing chosen gives the empty file. <c>value = ""</c> clears the choice and anything
    /// else throws Chromium's <c>InvalidStateError</c>.
    /// </summary>
    [Fact]
    public void TheChosenFilesAreTheFormsAndScriptsFiles()
    {
        using var session = Start(string.Empty);
        session.SettleLoadWindow();
        session.SetFilesByUser(0, [File("a.txt", "text/plain", "hello")]);

        session.RunJavaScriptUrl("javascript:void (function () {" +
            "var fd = new FormData(document.getElementById('f'));" +
            "note(Array.from(fd).map(function (e) { return e[0] + '=' + (e[1] instanceof File ? 'File(' + e[1].name + ',' + e[1].type + ',' + e[1].size + ')' : e[1]); }).join('&'));" +
            "note(fd.get('one') === one.files[0]);" +
            "one.files[0].text().then(function (t) { note('text ' + t);" +
            "  try { one.value = 'x'; } catch (e) { note(e.name + ': ' + e.message); }" +
            "  one.value = ''; note('cleared ' + one.files.length + ' ' + JSON.stringify(one.value)); });" +
            "})()");

        Assert.Equal(
            "one=File(a.txt,text/plain,5)&many=File(,application/octet-stream,0)|true|text hello|" +
            "InvalidStateError: Failed to set the 'value' property on 'HTMLInputElement': This input element accepts a filename, which may only be programmatically set to the empty string.|" +
            "cleared 0 \"\"",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>A picker closed without a choice fires <c>cancel</c> at its input, bubbling.</summary>
    [Fact]
    public void AClosedPickerFiresCancel()
    {
        using var session = Start(
            "document.getElementById('f').addEventListener('cancel', function (e) { note('cancel ' + e.target.id + ' ' + e.isTrusted + ' ' + e.bubbles + ' ' + e.cancelable); });");
        session.SettleLoadWindow();

        Assert.True(session.CancelFilePickByUser(1));
        Assert.False(session.CancelFilePickByUser(2));

        Assert.Equal("cancel many true true false", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }
}
