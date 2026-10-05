using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A <c>Worker</c> is an <c>EventTarget</c>: a listener added with <c>addEventListener</c> can be taken
/// off again with <c>removeEventListener</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>removeEventListener</c> was missing, so calling it was a <c>TypeError</c>.</b> reCAPTCHA's
/// checkbox frame starts a worker and waits five seconds for its first message; the timer that gives
/// up calls <c>worker.removeEventListener('message', ...)</c> before rejecting the promise it was
/// waiting on. It threw instead, the promise was never settled, and the widget never finished loading:
/// the page reported "reCAPTCHA Timeout".
/// </para>
/// <para>
/// The worker here is one whose script cannot be had -- a local file, which an <c>https:</c> page
/// has none read for it -- so the <c>error</c> event it fires is the event listened for, no worker
/// thread is involved, and nothing is asked of the network.
/// </para>
/// </remarks>
public class WorkerEventListenerTests
{
    private const string PageUrl = "https://example.test/worker-listeners";
    private const string PageHtml = "<html><body><div id=\"out\"></div></body></html>";

    private static string Run(string script) =>
        PageProbe.OutOf(PageProbe.Render([script], PageHtml, PageUrl));

    /// <summary>
    /// A listener taken off before the event fires does not run, and the one left on does. The removed
    /// one ran too, after the call to remove it had thrown.
    /// </summary>
    [Fact]
    public void AListenerTakenOffAWorkerDoesNotRun()
    {
        Assert.Equal(
            "ran=kept",
            Run("""
                var out = document.getElementById('out');
                var ran = [];
                function removed() { ran.push('removed'); out.textContent = 'ran=' + ran.join(','); }
                function kept() { ran.push('kept'); out.textContent = 'ran=' + ran.join(','); }
                var worker = new Worker('file:///broiler-test/worker.js');
                worker.addEventListener('error', removed);
                worker.addEventListener('error', kept);
                worker.removeEventListener('error', removed);
                """));
    }

    /// <summary>
    /// Only the listener named, for the type named, is taken off: naming it for another type, or
    /// naming another function for this type, takes nothing off. Both listeners ran, the first call to
    /// remove one having thrown.
    /// </summary>
    [Fact]
    public void OnlyTheListenerNamedForTheTypeNamedIsTakenOff()
    {
        Assert.Equal(
            "ran=listener",
            Run("""
                var out = document.getElementById('out');
                var ran = [];
                function listener() { ran.push('listener'); out.textContent = 'ran=' + ran.join(','); }
                function other() { ran.push('other'); out.textContent = 'ran=' + ran.join(','); }
                var worker = new Worker('file:///broiler-test/worker.js');
                worker.addEventListener('error', listener);
                worker.addEventListener('error', other);
                worker.removeEventListener('message', listener);
                worker.removeEventListener('error', function () {});
                worker.removeEventListener('error', other);
                """));
    }
}
