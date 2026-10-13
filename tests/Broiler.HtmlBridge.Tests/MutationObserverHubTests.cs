using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Direct unit tests for the observer authority (<see cref="MutationObserverHub"/>): registration
/// (with <c>observe()</c>'s replace semantics), disconnect, snapshotting and clear. End-to-end
/// delivery is covered by the MutationObserver tests that run page script.
/// </summary>
public class MutationObserverHubTests
{
    private static DomNode NewNode() => new DomDocument().CreateElement("div");

    private static DomMutationObserverOptions ChildListOptions() => new() { ChildList = true };

    [Fact]
    public void RegisterAddsAnObserver()
    {
        using var realm = TestRealm.Create();
        var hub = new MutationObserverHub();
        var observer = realm.NewObject();
        var target = NewNode();

        hub.Register(observer, target, ChildListOptions());

        Assert.Equal(1, hub.Count);
        var snapshot = hub.Snapshot();
        Assert.Single(snapshot);
        Assert.True(snapshot[0].Observer == observer);
        Assert.Same(target, snapshot[0].Target);
    }

    [Fact]
    public void ObservingTheSameTargetAgainReplacesTheOptions()
    {
        using var realm = TestRealm.Create();
        var hub = new MutationObserverHub();
        var observer = realm.NewObject();
        var target = NewNode();
        var newOptions = ChildListOptions();

        hub.Register(observer, target, ChildListOptions());
        hub.Register(observer, target, newOptions);

        Assert.Equal(1, hub.Count); // not duplicated
        Assert.Same(newOptions, hub.Snapshot()[0].Options);
    }

    [Fact]
    public void OneObserverOnTwoTargetsIsTwoRegistrations()
    {
        using var realm = TestRealm.Create();
        var hub = new MutationObserverHub();
        var observer = realm.NewObject();

        hub.Register(observer, NewNode(), ChildListOptions());
        hub.Register(observer, NewNode(), ChildListOptions());

        Assert.Equal(2, hub.Count);
    }

    [Fact]
    public void UnregisterRemovesEveryRegistrationOfTheObserver()
    {
        using var realm = TestRealm.Create();
        var hub = new MutationObserverHub();
        var observer = realm.NewObject();
        var other = realm.NewObject();
        hub.Register(observer, NewNode(), ChildListOptions());
        hub.Register(observer, NewNode(), ChildListOptions());
        hub.Register(other, NewNode(), ChildListOptions());

        hub.Unregister(observer);

        // Only the other observer's registration is left: the handle is matched by identity.
        Assert.Equal(1, hub.Count);
        Assert.True(hub.Snapshot()[0].Observer == other);
    }

    [Fact]
    public void ClearDropsEveryObserver()
    {
        using var realm = TestRealm.Create();
        var hub = new MutationObserverHub();
        hub.Register(realm.NewObject(), NewNode(), ChildListOptions());
        hub.Register(realm.NewObject(), NewNode(), ChildListOptions());

        hub.Clear();

        Assert.Equal(0, hub.Count);
        Assert.Empty(hub.Snapshot());
    }
}
