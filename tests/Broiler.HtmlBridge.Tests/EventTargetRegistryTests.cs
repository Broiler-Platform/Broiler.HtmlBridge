using Broiler.Dom;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using static Broiler.HtmlBridge.Tests.PageProbe;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the listener authority (<see cref="EventTargetRegistry"/>): direct unit tests of the
/// node, window and generic-target listener stores, the owner-window map and the visual-viewport
/// scroll listeners, plus the two characterizations that element and window listeners still fire
/// when page script dispatches to them.
/// </summary>
public class EventTargetRegistryTests
{
    private const string Page = "<!DOCTYPE html><html><body><div id='t'></div><div id=\"out\"></div></body></html>";

    private static DomElement NewElement() => new DomDocument().CreateElement("div");

    private static EventListenerRegistration NewRegistration(IJsRealm realm) =>
        new(realm.Callback(() => { }), capture: false);

    // ------------------------------------------------------------------
    //  Node listeners
    // ------------------------------------------------------------------

    [Fact]
    public void EachNodeHasOneStableStore()
    {
        var registry = new EventTargetRegistry();
        var a = NewElement();
        var b = NewElement();

        Assert.Same(registry.NodeListeners(a), registry.NodeListeners(a));
        Assert.NotSame(registry.NodeListeners(a), registry.NodeListeners(b));
    }

    [Fact]
    public void NodeListenersAreKeyedByTypeCaseInsensitively()
    {
        var registry = new EventTargetRegistry();
        var node = NewElement();

        registry.NodeListeners(node)["Click"] = [];

        Assert.True(registry.NodeListeners(node).ContainsKey("click"));
    }

    // ------------------------------------------------------------------
    //  Window listeners
    // ------------------------------------------------------------------

    [Fact]
    public void WindowListenersAreCreatedOnAddAndFoundCaseInsensitively()
    {
        using var realm = TestRealm.Create();
        var registry = new EventTargetRegistry();
        Assert.False(registry.TryGetWindowListeners("load", out _));

        var list = registry.WindowListenersForAdd("load");
        list.Add(NewRegistration(realm));

        Assert.True(registry.TryGetWindowListeners("LOAD", out var found));
        Assert.Same(list, found);
    }

    // ------------------------------------------------------------------
    //  Generic-target listeners & owner windows
    // ------------------------------------------------------------------

    [Fact]
    public void TargetListenersAreKeyedByTheTargetsIdentity()
    {
        using var realm = TestRealm.Create();
        var registry = new EventTargetRegistry();
        var target = realm.NewObject();
        var other = realm.NewObject();

        registry.TargetListenersForAdd(target)["message"] = [];

        Assert.True(registry.TryGetTargetListeners(target, out var byType));
        Assert.True(byType.ContainsKey("message"));
        Assert.False(registry.TryGetTargetListeners(other, out _));
    }

    [Fact]
    public void AnOwnerWindowIsFoundForTheTargetItWasSetOn()
    {
        using var realm = TestRealm.Create();
        var registry = new EventTargetRegistry();
        var target = realm.NewObject();
        var window = realm.NewObject();
        Assert.False(registry.TryGetOwnerWindow(target, out _));

        registry.SetOwnerWindow(target, window);

        Assert.True(registry.TryGetOwnerWindow(target, out var found));
        Assert.True(found == window);
    }

    // ------------------------------------------------------------------
    //  Visual-viewport scroll listeners
    // ------------------------------------------------------------------

    [Fact]
    public void AVisualViewportScrollListenerIsAddedOnceAndCanBeRemoved()
    {
        using var realm = TestRealm.Create();
        var registry = new EventTargetRegistry();
        var listener = realm.Callback(() => { });

        registry.AddVisualViewportScrollListener(listener);
        registry.AddVisualViewportScrollListener(listener); // duplicate ignored

        Assert.Single(registry.VisualViewportScrollListeners);
        registry.RemoveVisualViewportScrollListener(listener);
        Assert.Empty(registry.VisualViewportScrollListeners);
    }

    // ------------------------------------------------------------------
    //  Clear
    // ------------------------------------------------------------------

    [Fact]
    public void ClearDropsEveryStore()
    {
        using var realm = TestRealm.Create();
        var registry = new EventTargetRegistry();
        var node = NewElement();
        var target = realm.NewObject();
        registry.NodeListeners(node)["click"] = [NewRegistration(realm)];
        registry.WindowListenersForAdd("load").Add(NewRegistration(realm));
        registry.TargetListenersForAdd(target)["message"] = [NewRegistration(realm)];
        registry.SetOwnerWindow(target, realm.NewObject());
        registry.AddVisualViewportScrollListener(realm.Callback(() => { }));

        registry.Clear();

        Assert.Empty(registry.NodeListeners(node)); // a fresh store after clear
        Assert.False(registry.TryGetWindowListeners("load", out _));
        Assert.False(registry.TryGetTargetListeners(target, out _));
        Assert.False(registry.TryGetOwnerWindow(target, out _));
        Assert.Empty(registry.VisualViewportScrollListeners);
    }

    // ------------------------------------------------------------------
    //  Characterization through page script
    // ------------------------------------------------------------------

    [Fact]
    public void AnElementListenerFiresOnDispatch()
    {
        var hits = RunAgainst(Page, "file:///e.html", """
            (function () {
              var hits = 0;
              var t = document.getElementById('t');
              t.addEventListener('ping', function () { hits++; });
              var e = document.createEvent('Event'); e.initEvent('ping', true, true);
              t.dispatchEvent(e);
              return hits;
            })()
            """);

        Assert.Equal("1", hits);
    }

    [Fact]
    public void AWindowListenerFiresOnDispatch()
    {
        var hits = RunAgainst(Page, "file:///w.html", """
            (function () {
              var hits = 0;
              window.addEventListener('ping', function () { hits++; });
              var e = document.createEvent('Event'); e.initEvent('ping', false, false);
              window.dispatchEvent(e);
              return hits;
            })()
            """);

        Assert.Equal("1", hits);
    }
}
