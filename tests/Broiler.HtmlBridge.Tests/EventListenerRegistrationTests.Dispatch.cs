using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Tests;

public partial class EventListenerRegistrationTests
{
    public static IEnumerable<object[]> DispatchTargets =>
        new[] { "element", "document", "window", "port", "frame" }.Select(target => new object[] { target });

    private static string RunDispatch(string target, string script)
    {
        var expression = target switch
        {
            "element" => "document.getElementById('host')",
            "document" => "document",
            "window" => "window",
            "port" => "new MessageChannel().port1",
            "frame" => """
                (function () {
                    var frame = document.createElement('iframe');
                    frame.srcdoc = '<html><body></body></html>';
                    document.body.appendChild(frame);
                    return frame.contentWindow;
                })()
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(target))
        };
        return Run($"(function () {{ var target = {expression}; {script} }})()");
    }

    [Theory]
    [MemberData(nameof(DispatchTargets))]
    public void OnceIsRemovedBeforeNestedDispatch(string target)
    {
        Assert.Equal("1", RunDispatch(target, """
            var calls = 0;
            target.addEventListener('ping', function () {
                calls++;
                if (calls < 2) target.dispatchEvent(new Event('ping'));
            }, { once: true });
            target.dispatchEvent(new Event('ping'));
            return String(calls);
            """));
    }

    [Theory]
    [MemberData(nameof(DispatchTargets))]
    public void RemovingAListenerSkipsItInTheCurrentDispatch(string target)
    {
        Assert.Equal("A", RunDispatch(target, """
            var seen = '';
            var second = function () { seen += 'B'; };
            target.addEventListener('ping', function () {
                seen += 'A';
                target.removeEventListener('ping', second);
            });
            target.addEventListener('ping', second);
            target.dispatchEvent(new Event('ping'));
            return seen;
            """));
    }

    [Theory]
    [MemberData(nameof(DispatchTargets))]
    public void RemovingAndReaddingTheSameListenerDoesNotReviveTheSnapshot(string target)
    {
        Assert.Equal("A|B", RunDispatch(target, """
            var seen = '';
            var second = function () { seen += 'B'; };
            target.addEventListener('ping', function () {
                seen += 'A';
                target.removeEventListener('ping', second);
                target.addEventListener('ping', second);
            }, { once: true });
            target.addEventListener('ping', second);
            target.dispatchEvent(new Event('ping'));
            seen += '|';
            target.dispatchEvent(new Event('ping'));
            return seen;
            """));
    }

    [Theory]
    [MemberData(nameof(DispatchTargets))]
    public void AOnceListenerCanRegisterItselfAgain(string target)
    {
        Assert.Equal("1/2/2", RunDispatch(target, """
            var calls = 0;
            var listener = function () {
                calls++;
                if (calls === 1) target.addEventListener('ping', listener, { once: true });
            };
            target.addEventListener('ping', listener, { once: true });
            target.dispatchEvent(new Event('ping'));
            var seen = String(calls);
            target.dispatchEvent(new Event('ping'));
            seen += '/' + calls;
            target.dispatchEvent(new Event('ping'));
            return seen + '/' + calls;
            """));
    }

    [Theory]
    [MemberData(nameof(DispatchTargets))]
    public void NewListenersWaitForTheNextDispatch(string target)
    {
        Assert.Equal("AB|BC", RunDispatch(target, """
            var seen = '';
            target.addEventListener('ping', function () {
                seen += 'A';
                target.addEventListener('ping', function () { seen += 'C'; });
            }, { once: true });
            target.addEventListener('ping', function () { seen += 'B'; });
            target.dispatchEvent(new Event('ping'));
            seen += '|';
            target.dispatchEvent(new Event('ping'));
            return seen;
            """));
    }

    [Theory]
    [MemberData(nameof(DispatchTargets))]
    public void StoppingDispatchDoesNotConsumeUninvokedOnceListeners(string target)
    {
        Assert.Equal("A|B|", RunDispatch(target, """
            var seen = '';
            target.addEventListener('ping', function (event) {
                seen += 'A';
                event.stopImmediatePropagation();
            }, { once: true });
            target.addEventListener('ping', function () { seen += 'B'; }, { once: true });
            target.dispatchEvent(new Event('ping'));
            seen += '|';
            target.dispatchEvent(new Event('ping'));
            seen += '|';
            target.dispatchEvent(new Event('ping'));
            return seen;
            """));
    }

    [Theory]
    [MemberData(nameof(DispatchTargets))]
    public void PassiveStateIsScopedToTheCurrentListener(string target)
    {
        Assert.Equal("passive=false active=true result=false", RunDispatch(target, """
            var seen = '';
            target.addEventListener('ping', function (event) {
                event.preventDefault();
                seen += 'passive=' + event.defaultPrevented;
            }, { passive: true });
            target.addEventListener('ping', function (event) {
                event.preventDefault();
                seen += ' active=' + event.defaultPrevented;
            });
            var result = target.dispatchEvent(new Event('ping', { cancelable: true }));
            return seen + ' result=' + result;
            """));
    }

    [Theory]
    [MemberData(nameof(DispatchTargets))]
    public void ThrowingOnceListenersStayRemovedAndDoNotStopOtherListeners(string target)
    {
        Assert.Equal("AB|B", RunDispatch(target, """
            var seen = '';
            target.addEventListener('ping', function () {
                seen += 'A';
                throw new Error('listener failure');
            }, { once: true });
            target.addEventListener('ping', function () { seen += 'B'; });
            target.dispatchEvent(new Event('ping'));
            seen += '|';
            target.dispatchEvent(new Event('ping'));
            return seen;
            """));
    }

    [Fact]
    public void FormSubmitUsesTheSameOnceAndRemovalRules()
    {
        // submit() fires no submit event at all, so the once listener is still there for the
        // requestSubmit() after it, which fires one under the ordinary rules; one asked for from the
        // form's own submit listener is ignored.
        Assert.Equal("A by requestSubmit", Run("""
            (function () {
                var form = document.createElement('form');
                document.body.appendChild(form);
                var seen = '', by = '';
                var second = function () { seen += 'B'; };
                form.addEventListener('submit', function (e) {
                    e.preventDefault();
                    seen += 'A by ' + by;
                    form.removeEventListener('submit', second);
                    form.requestSubmit();
                }, { once: true });
                form.addEventListener('submit', second);
                by = 'submit'; form.submit();
                by = 'requestSubmit'; form.requestSubmit();
                form.requestSubmit();
                return seen;
            })()
            """));
    }

    [Theory]
    [InlineData("node")]
    [InlineData("window")]
    [InlineData("generic")]
    public void ResettingTheRegistryStopsAnActiveDispatch(string store)
    {
        var registry = new EventTargetRegistry();
        var node = new DomDocument();
        var lists = store switch
        {
            "node" => registry.NodeListeners(node),
            "generic" => registry.TargetListenersForAdd(JsValue.Null),
            _ => null
        };
        List<EventListenerRegistration> listeners;
        if (lists is null)
            listeners = registry.WindowListenersForAdd("ping");
        else
            lists["ping"] = listeners = [];

        // The callback adapter does not need an engine for this lifecycle test.
        listeners.Add(new EventListenerRegistration(JsValue.Undefined, false));
        listeners.Add(new EventListenerRegistration(JsValue.Undefined, false));
        var calls = 0;
        var immediateStopped = false;
        var passive = false;
        EventListenerBinding.InvokeListeners(listeners, _ =>
        {
            calls++;
            registry.Clear();
        }, ref immediateStopped, ref passive);

        Assert.Equal(1, calls);
        GC.KeepAlive(node);
    }
}
