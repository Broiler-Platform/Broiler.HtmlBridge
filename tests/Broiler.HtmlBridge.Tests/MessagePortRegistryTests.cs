using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Direct unit tests for the message-port authority (<see cref="MessagePortRegistry"/>):
/// entanglement, the close and start marks, and the queue-then-deliver flow. End-to-end
/// <c>MessageChannel</c> delivery is covered by the port tests that run page script
/// (<c>WorkerPortTests</c>, <c>FramePostMessageTests</c>).
/// </summary>
public class MessagePortRegistryTests
{
    [Fact]
    public void LinkEntanglesBothEnds()
    {
        using var realm = TestRealm.Create();
        var registry = new MessagePortRegistry();
        var a = realm.NewObject();
        var b = realm.NewObject();

        registry.Link(a, b);

        Assert.True(registry.IsPort(a));
        Assert.True(registry.IsPort(b));
        Assert.True(registry.TryGetPeer(a, out var peerOfA));
        Assert.True(peerOfA == b);
        Assert.True(registry.TryGetPeer(b, out var peerOfB));
        Assert.True(peerOfB == a);
    }

    [Fact]
    public void AnUnlinkedPortHasNoPeer()
    {
        using var realm = TestRealm.Create();
        var registry = new MessagePortRegistry();

        Assert.False(registry.IsPort(realm.NewObject()));
        Assert.False(registry.TryGetPeer(realm.NewObject(), out _));
    }

    [Fact]
    public void CloseMarksThePortClosedAndDropsWhatWasQueued()
    {
        using var realm = TestRealm.Create();
        var registry = new MessagePortRegistry();
        var port = realm.NewObject();
        registry.Enqueue(port, realm.NewObject());

        registry.Close(port);

        Assert.True(registry.IsClosed(port));
        Assert.True(registry.IsInert(port));
        Assert.Null(registry.TakeQueued(port)); // queued messages dropped on close
    }

    [Fact]
    public void StartMarksThePortStarted()
    {
        using var realm = TestRealm.Create();
        var registry = new MessagePortRegistry();
        var port = realm.NewObject();

        Assert.False(registry.IsStarted(port));
        registry.Start(port);
        Assert.True(registry.IsStarted(port));
    }

    [Fact]
    public void QueuedMessagesAreTakenInOrderAndOnlyOnce()
    {
        using var realm = TestRealm.Create();
        var registry = new MessagePortRegistry();
        var port = realm.NewObject();
        var first = realm.NewObject();
        var second = realm.NewObject();
        registry.Enqueue(port, first);
        registry.Enqueue(port, second);

        var taken = registry.TakeQueued(port);

        Assert.NotNull(taken);
        Assert.Equal(2, taken!.Count);
        Assert.True(taken[0] == first);
        Assert.True(taken[1] == second);
        Assert.Null(registry.TakeQueued(port)); // drained
    }

    [Fact]
    public void TakingFromAnEmptyQueueAnswersNull()
    {
        using var realm = TestRealm.Create();

        Assert.Null(new MessagePortRegistry().TakeQueued(realm.NewObject()));
    }

    [Fact]
    public void ClearDropsAllPortState()
    {
        using var realm = TestRealm.Create();
        var registry = new MessagePortRegistry();
        var a = realm.NewObject();
        var b = realm.NewObject();
        registry.Link(a, b);
        registry.Start(a);
        registry.Close(b);
        registry.Enqueue(a, realm.NewObject());

        registry.Clear();

        Assert.False(registry.IsPort(a));
        Assert.False(registry.IsStarted(a));
        Assert.False(registry.IsClosed(b));
        Assert.Null(registry.TakeQueued(a));
    }
}
