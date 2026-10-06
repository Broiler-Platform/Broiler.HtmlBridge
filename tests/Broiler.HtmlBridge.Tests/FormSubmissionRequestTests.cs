using System.Text;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The request the page's own form submission makes is the page's: encoded from its form data set as it stands
/// when the form is submitted -- what a file input holds, what a select has selected, what a script changed after
/// the user -- rather than built again by the host from the markup and its own record of the user's choices.
/// </summary>
/// <remarks>
/// The window built it again: it read a file input's files from disk where the user had picked them and took a
/// select's option from what the user had chosen, so a script that cleared the input, or changed the select in its
/// <c>change</c> listener, changed nothing that was sent. Chromium sends what the form holds (HTML "constructing the
/// entry list").
/// </remarks>
public class FormSubmissionRequestTests
{
    private const string PageUrl = "https://example.test/form";

    private static InteractiveSession Start(string body, string script)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        var session = engine.ExecuteInteractive([script], [], $"<html><body>{body}</body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    /// <summary>A <c>get</c> carries its entries in its URL: the select the script changed after the user, the field's value.</summary>
    [Fact]
    public void AGetSendsWhatTheFormHoldsNow()
    {
        using var session = Start(
            "<form id=\"f\" action=\"/result\"><input name=\"q\" value=\"typed\">" +
            "<select id=\"s\" name=\"s\"><option value=\"a\">A</option><option value=\"b\">B</option><option value=\"c\">C</option></select></form>",
            "var s = document.getElementById('s'); s.addEventListener('change', function () { if (s.value === 'b') s.value = 'c'; });");
        session.SettleLoadWindow();

        Assert.True(session.SelectOptionByUser(0, 1));
        session.RunJavaScriptUrl("javascript:void document.getElementById('f').submit()");
        session.SettleLoadWindow();

        var pending = session.TakePendingNavigation();
        Assert.Equal(NavigationKind.FormSubmit, pending?.Kind);
        Assert.Equal("https://example.test/result", pending!.Url);
        Assert.Equal("https://example.test/result?q=typed&s=c", pending.Submission?.Url);
        Assert.Null(pending.Submission!.Body);
    }

    /// <summary>
    /// A <c>post</c> carries them as its body: a file input the script cleared after the user picked a file sends an
    /// empty file part, and one it left sends the bytes the user picked.
    /// </summary>
    [Fact]
    public void APostSendsTheFilesTheFormHoldsNow()
    {
        using var session = Start(
            "<form id=\"f\" action=\"/upload\" method=\"post\" enctype=\"multipart/form-data\">" +
            "<input type=\"file\" id=\"kept\" name=\"kept\"><input type=\"file\" id=\"cleared\" name=\"cleared\"></form>",
            "document.getElementById('cleared').addEventListener('change', function (e) { e.target.value = ''; });");
        session.SettleLoadWindow();

        Assert.True(session.SetFilesByUser(0, [new ChosenFile("kept.txt", "text/plain", DateTimeOffset.UnixEpoch, Encoding.UTF8.GetBytes("kept bytes"))]));
        Assert.True(session.SetFilesByUser(1, [new ChosenFile("gone.txt", "text/plain", DateTimeOffset.UnixEpoch, Encoding.UTF8.GetBytes("gone bytes"))]));
        session.SettleLoadWindow();
        session.RunJavaScriptUrl("javascript:void document.getElementById('f').submit()");
        session.SettleLoadWindow();

        var submission = session.TakePendingNavigation()?.Submission;
        Assert.NotNull(submission);
        Assert.Equal("https://example.test/upload", submission!.Url);
        Assert.StartsWith("multipart/form-data; boundary=", submission.BodyContentType, StringComparison.Ordinal);

        var body = Encoding.UTF8.GetString(submission.Body!);
        Assert.Contains("filename=\"kept.txt\"", body, StringComparison.Ordinal);
        Assert.Contains("kept bytes", body, StringComparison.Ordinal);
        Assert.DoesNotContain("gone", body, StringComparison.Ordinal);
        Assert.Contains("name=\"cleared\"; filename=\"\"", body, StringComparison.Ordinal);
    }
}
