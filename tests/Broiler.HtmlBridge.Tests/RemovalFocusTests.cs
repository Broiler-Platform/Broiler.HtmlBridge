using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A focused element, or anything holding one, removed from the document: Chromium blurs it first, while it
/// is still there. Measured: <c>blur</c> and then <c>focusout</c>, trusted,
/// with no <c>relatedTarget</c>, synchronously inside the call that removes it, the element still connected
/// and its parent unchanged, <c>document.activeElement</c> already the body -- for <c>remove()</c>, an
/// ancestor's removal, <c>innerHTML</c>, <c>replaceWith</c> and a move by <c>appendChild</c> alike. A
/// <c>blur</c> handler that moves the element makes the removal throw <c>NotFoundError</c>.
/// </summary>
/// <remarks>
/// Only the focus fixup took focus away, at the next frame and in a task of its own (DomBridge/Focus.cs), by
/// when the element had gone: a page that saves a field on <c>blur</c> saved it from outside the document.
/// </remarks>
public class RemovalFocusTests
{
    private const string PageUrl = "https://example.test/removal-focus";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }" +
        "function ae() { var a = document.activeElement; return a ? (a.id || a.tagName) : 'none'; }" +
        "var i = document.getElementById('i'), wrap = document.getElementById('wrap'), other = document.getElementById('other');";

    private const string Body = "<div id=\"wrap\"><input id=\"i\"></div><div id=\"other\"></div>";

    private static string Settle(string script)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        using var session = engine.ExecuteInteractive(
            [Recorder + script + ";show();"], [], $"<html id=\"root\"><body id=\"body\">{Body}<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    [Fact]
    public void RemovingTheFocusedElementBlursItWhileItIsStillThere()
    {
        var log = Settle(
            "['blur', 'focusout'].forEach(function (t) { i.addEventListener(t, function (e) {" +
            "  note(t + ' ae=' + ae() + ' connected=' + i.isConnected + ' parent=' + (i.parentNode && i.parentNode.id) +" +
            "    ' trusted=' + e.isTrusted + ' related=' + e.relatedTarget); }); });" +
            "wrap.addEventListener('focusout', function () { note('focusout at wrap'); });" +
            "i.focus(); note('before ' + ae()); i.remove(); note('after ' + ae() + ' connected=' + i.isConnected);" +
            "setTimeout(function () { note('later ' + ae()); }, 50);");

        Assert.Equal(
            "before i|blur ae=body connected=true parent=wrap trusted=true related=null|" +
            "focusout ae=body connected=true parent=wrap trusted=true related=null|focusout at wrap|" +
            "after body connected=false|later body",
            log);
    }

    /// <summary>Every way out of the document blurs first: the element's removal, an ancestor's, a replace-all, a move.</summary>
    [Theory]
    [InlineData("i.remove()", "false")]
    [InlineData("wrap.removeChild(i)", "false")]
    [InlineData("wrap.remove()", "false")]
    [InlineData("document.body.removeChild(wrap)", "false")]
    [InlineData("wrap.innerHTML = ''", "false")]
    [InlineData("wrap.textContent = ''", "false")]
    [InlineData("wrap.replaceChildren()", "false")]
    [InlineData("i.replaceWith(document.createElement('span'))", "false")]
    [InlineData("wrap.replaceChild(document.createElement('span'), i)", "false")]
    [InlineData("other.appendChild(i)", "true")]
    [InlineData("other.insertBefore(i, null)", "true")]
    public void EveryRemovalBlursFirst(string removal, string connectedAfter)
    {
        var log = Settle(
            "i.addEventListener('blur', function () { note('blur connected=' + i.isConnected); });" +
            "i.focus(); " + removal + "; note('after ' + ae() + ' ' + i.isConnected);" +
            "setTimeout(function () { note('later ' + ae()); }, 50);");

        Assert.Equal($"blur connected=true|after body {connectedAfter}|later body", log);
    }

    /// <summary>
    /// A <c>blur</c> handler that moves the element makes the removal throw, as Chromium's does, and the element
    /// stays where the handler put it.
    /// </summary>
    [Theory]
    [InlineData("i.remove()", "Failed to execute 'remove' on 'Element'")]
    [InlineData("wrap.removeChild(i)", "Failed to execute 'removeChild' on 'Node'")]
    public void ABlurHandlerThatMovesTheElementMakesTheRemovalThrow(string removal, string prefix)
    {
        var log = Settle(
            "i.addEventListener('blur', function () { other.appendChild(i); });" +
            "i.focus();" +
            "try { " + removal + "; note('no throw'); } catch (e) { note(e.name + ': ' + e.message + ' dom=' + (e instanceof DOMException)); }" +
            "note('parent ' + i.parentNode.id + ' ' + ae());");

        Assert.Equal(
            "NotFoundError: " + prefix + ": The node to be removed is no longer a child of this node. " +
            "Perhaps it was moved in a 'blur' event handler? dom=true|parent other body",
            log);
    }

    /// <summary>Controls, which pass before and after: removing something else, or an unfocused field, moves no focus.</summary>
    [Theory]
    [InlineData("other.remove()", "after i")]
    [InlineData("i.blur(); i.remove()", "blur|after body")]
    public void ARemovalThatTakesNoFocusFiresNothing(string removal, string expected)
    {
        var log = Settle(
            "i.addEventListener('blur', function () { note('blur'); });" +
            "i.focus(); " + removal + "; note('after ' + ae());");

        Assert.Equal(expected, log);
    }
}
