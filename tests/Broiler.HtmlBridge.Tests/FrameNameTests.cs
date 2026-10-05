using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A frame's browsing-context name: <c>window.name</c> in the frame, set from its container's
/// <c>name</c> attribute and changeable by the frame itself, and the frame found by it through its
/// parent's <c>frames</c> -- plus the <c>frames</c> and <c>length</c> of a frame's own window.
/// </summary>
/// <remarks>
/// <para>
/// <b>A frame's window had no name, and <c>frames</c> had no names in it.</b> reCAPTCHA's challenge
/// frame finds the checkbox frame beside it by name: it reads its own <c>window.name</c>, turns the
/// <c>c-</c> prefix into <c>a-</c> and looks the result up in <c>parent.frames</c>. Here the name read
/// as <c>undefined</c>, so its <c>replace</c> threw, and <c>frames</c> was an array of windows with
/// nothing to look a name up in.
/// </para>
/// <para>
/// The frames are <c>srcdoc</c> documents, so no network is involved, and they write into the page's
/// <c>#out</c> through <c>pageOut</c>, a global the page's script sets: every document shares one realm.
/// A frame's script runs when its window is first needed, which is when the page's script first
/// touches it. The frames' scripts contain no double quote, so the double-quoted <c>srcdoc</c>
/// attribute carries them unescaped.
/// </para>
/// </remarks>
public class FrameNameTests
{
    private const string PageUrl = "https://example.test/frame-names";

    private static string Frame(string attributes, string script = "", string body = "") =>
        $"<iframe {attributes} srcdoc=\"<html><body>{body}<script>{script}</script></body></html>\"></iframe>";

    private static string Run(string frames, string pageScript) =>
        PageProbe.OutOf(PageProbe.Render(
            [pageScript],
            $"<html><body>{frames}<div id=\"out\"></div></body></html>",
            PageUrl));

    /// <summary>
    /// A frame's window is named by its container's <c>name</c> attribute, however the page reaches it,
    /// and an unnamed frame's name, like the page's own, is the empty string. They all read
    /// <c>undefined</c>.
    /// </summary>
    [Fact]
    public void AFrameIsNamedByItsContainersNameAttribute()
    {
        Assert.Equal(
            "a-xyz|a-xyz|()|()",
            Run(
                Frame("id=\"a\" name=\"a-xyz\"") + Frame("id=\"b\""),
                "document.getElementById('out').textContent = [frames[0].name," +
                " document.getElementById('a').contentWindow.name," +
                " '(' + document.getElementById('b').contentWindow.name + ')'," +
                " '(' + window.name + ')'].join('|');"));
    }

    /// <summary>
    /// A frame's script reads its own name however it spells the window: <c>window</c>, <c>self</c>,
    /// a bare <c>name</c>, and <c>this</c> at its top level -- the global object every document here
    /// shares, which is what Closure's <c>goog.global</c> is.
    /// </summary>
    [Fact]
    public void AFramesScriptReadsItsOwnName()
    {
        Assert.Equal(
            "a-xyz|a-xyz|a-xyz|a-xyz",
            Run(
                Frame("name=\"a-xyz\"",
                    "pageOut.textContent = [window.name, self.name, name, this.name].join('|');"),
                "var pageOut = document.getElementById('out');" +
                "pageOut.textContent = 'frame-never-ran';" +
                "frames[0];"));
    }

    /// <summary>
    /// A frame renames itself by assigning its <c>window.name</c>, or a bare <c>name</c>; the page sees
    /// the new name and finds the frame by it, not by the old one, and the container's attribute is not
    /// changed. The page's own name is its own: renaming the frame does not rename the page, nor the
    /// other way round.
    /// </summary>
    [Fact]
    public void AFrameRenamesItselfAndIsFoundByItsNewName()
    {
        Assert.Equal(
            "renamed-twice|true|true|a-xyz|page-name",
            Run(
                Frame("id=\"a\" name=\"a-xyz\"", "window.name = 'renamed'; name = name + '-twice';"),
                "window.name = 'page-name';" +
                "var w = document.getElementById('a').contentWindow;" +
                "document.getElementById('out').textContent = [w.name, frames['renamed-twice'] === w," +
                " frames['a-xyz'] === undefined, document.getElementById('a').getAttribute('name'), name].join('|');"));
    }

    /// <summary>
    /// The challenge frame's lookup, done the way reCAPTCHA does it: from inside one frame, its own name
    /// with the prefix changed, looked up in <c>parent.frames</c>, finds the sibling frame's window, whose
    /// document it can read. The name was <c>undefined</c>, so <c>replace</c> threw.
    /// </summary>
    [Fact]
    public void AFrameFindsItsSiblingByNameThroughParentFrames()
    {
        Assert.Equal(
            "true|ANCHOR",
            Run(
                Frame("id=\"a\" name=\"a-1\"", body: "<p id='in'>ANCHOR</p>") +
                Frame("id=\"c\" name=\"c-1\"",
                    "var anchor = parent.frames[window.name.replace('c-', 'a-')];" +
                    "pageOut.textContent = [anchor === anchorWindow, anchor.document.getElementById('in').textContent].join('|');"),
                "var pageOut = document.getElementById('out');" +
                "pageOut.textContent = 'frame-never-ran';" +
                "var anchorWindow = document.getElementById('a').contentWindow;" +
                "document.getElementById('c').contentWindow;"));
    }

    /// <summary>
    /// <c>frames</c> is live and is one object: a frame added later is found by its name, the first of
    /// two frames sharing a name is the one found, a name no frame has finds nothing, and
    /// <c>window.length</c> counts the frames. <c>frames</c> was a fresh array on every read.
    /// </summary>
    [Fact]
    public void FramesIsOneLiveListThatFindsTheFirstFrameWithAName()
    {
        Assert.Equal(
            "true|true|true|true|3|3",
            Run(
                Frame("id=\"first\" name=\"twin\"") + Frame("id=\"second\" name=\"twin\""),
                "var late = document.createElement('iframe');" +
                "late.name = 'late';" +
                "document.body.appendChild(late);" +
                "document.getElementById('out').textContent = [frames === frames," +
                " frames['twin'] === document.getElementById('first').contentWindow," +
                " frames['late'] === late.contentWindow, frames['nope'] === undefined," +
                " frames.length, window.length].join('|');"));
    }

    /// <summary>
    /// A frame's window has <c>frames</c> and <c>length</c> of its own: its document's frames, by index
    /// and by name. It had neither.
    /// </summary>
    [Fact]
    public void AFramesOwnFramesAreItsFramesByIndexAndName()
    {
        Assert.Equal(
            "1|1|true|inner",
            Run(
                Frame("id=\"outer\"", body: "<iframe name='inner' srcdoc='<p>inner</p>'></iframe>"),
                "var outer = document.getElementById('outer').contentWindow;" +
                "document.getElementById('out').textContent = [outer.frames.length, outer.length," +
                " outer.frames['inner'] === outer.frames[0], outer.frames[0].name].join('|');"));
    }

    /// <summary>
    /// <c>length</c> and <c>frames</c> are replaceable, as on a browser's window: the page's window is
    /// the global object, so a script's <c>var length = 3</c> assigns it, and has to read back 3 rather
    /// than the number of frames. Passed before the window had a <c>length</c>; guards against defining
    /// one without a setter.
    /// </summary>
    [Fact]
    public void AScriptsOwnLengthAndFramesVariablesAreItsOwn()
    {
        Assert.Equal(
            "3|mine",
            Run(
                Frame("id=\"a\""),
                "var length = 3; var frames = 'mine';" +
                "document.getElementById('out').textContent = [length, frames].join('|');"));
    }
}
