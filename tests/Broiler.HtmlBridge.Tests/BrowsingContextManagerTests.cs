using System.Reflection;
using Broiler.Dom;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the browsing-context state authority (<see cref="BrowsingContextManager"/>): the
/// sub-document cache, the deliberately asymmetric sub-window lifecycle, the session reset, the load
/// marks and the content-document links, plus an ownership guard that the scattered bridge fields it
/// replaced are gone. How the sub-window maps key on handles is <c>SubWindowMapKeyTests</c>'s.
/// </summary>
public class BrowsingContextManagerTests
{
    private static DomElement Frame() => new DomDocument().CreateElement("iframe");

    [Fact]
    public void ASubDocumentIsFoundForTheContainerItWasSetOn()
    {
        using var realm = TestRealm.Create();
        var manager = new BrowsingContextManager();
        var frame = Frame();
        var subDocument = realm.NewObject();
        Assert.False(manager.TryGetSubDocument(frame, out _));

        manager.SetSubDocument(frame, subDocument);

        Assert.True(manager.TryGetSubDocument(frame, out var found));
        Assert.True(found == subDocument);
    }

    [Fact]
    public void RemovingAContainersCachesRetiresItsWindowAndKeepsTheReverseEntry()
    {
        // A frame that navigates keeps its window, as a browsing context keeps its WindowProxy: the
        // forward entry is retired rather than dropped, and the window -> container entry stays until
        // the session resets.
        using var realm = TestRealm.Create();
        var manager = new BrowsingContextManager();
        var frame = Frame();
        var subWindow = realm.NewObject();
        manager.SetSubWindow(frame, subWindow);
        manager.SetLocation(frame, "about:srcdoc");
        manager.SetBaseUrl(frame, "file:///a");

        manager.RemoveContainerCaches(frame);

        Assert.False(manager.TryGetSubWindow(frame, out _));
        Assert.False(manager.TryGetLocation(frame, out _));
        Assert.False(manager.TryGetBaseUrl(frame, out _));
        Assert.True(manager.TryGetRetiredSubWindow(frame, out var retired));
        Assert.True(retired == subWindow);
        Assert.True(manager.IsSubWindow(subWindow));
        Assert.True(manager.TryGetSubWindowContainer(subWindow, out _));
    }

    [Fact]
    public void ResetSessionClearsTheReverseMapAndTheCurrentWindowButNotTheForwardCaches()
    {
        using var realm = TestRealm.Create();
        var manager = new BrowsingContextManager();
        var frame = Frame();
        var subWindow = realm.NewObject();
        manager.SetSubWindow(frame, subWindow);
        manager.SetSubDocument(frame, realm.NewObject());
        manager.CurrentWindowOverride = subWindow;

        manager.ResetSession();

        Assert.False(manager.IsSubWindow(subWindow));
        Assert.False(manager.CurrentWindowOverride.IsObject); // back to "the main window"
        Assert.True(manager.TryGetSubDocument(frame, out _)); // per-container caches keep their own lifecycle
        Assert.True(manager.TryGetSubWindow(frame, out _));
    }

    [Fact]
    public void LoadMarksTrackObjectFailureAndOnloadFired()
    {
        var manager = new BrowsingContextManager();
        var element = Frame();

        Assert.False(manager.HasObjectLoadFailed(element));
        manager.MarkObjectLoadFailed(element);
        Assert.True(manager.HasObjectLoadFailed(element));

        Assert.False(manager.HasOnloadFired(element));
        manager.MarkOnloadFired(element);
        Assert.True(manager.HasOnloadFired(element));
        manager.ClearOnloadFired(element);
        Assert.False(manager.HasOnloadFired(element));
    }

    [Fact]
    public void AContentDocumentIsLinkedAndUnlinkedInBothDirections()
    {
        var manager = new BrowsingContextManager();
        var frame = Frame();
        var document = new DomDocument();

        manager.LinkContentDocument(frame, document);
        Assert.Same(document, manager.GetContentDocument(frame));
        Assert.Same(frame, manager.GetContainerForDocument(document));

        Assert.Same(document, manager.UnlinkContentDocument(frame));
        Assert.Null(manager.GetContentDocument(frame));
        Assert.Null(manager.GetContainerForDocument(document));
        Assert.Null(manager.UnlinkContentDocument(frame)); // idempotent
    }

    [Fact]
    public void TheBridgeOwnsTheManagerAndNotTheFieldsItReplaced()
    {
        var bridgeFields = typeof(DomBridge)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(field => field.Name)
            .ToHashSet();

        Assert.Contains("_browsingContexts", bridgeFields);
        foreach (var gone in new[]
                 {
                     "_subDocumentCache", "_subWindowCache", "_subDocumentLocationCache",
                     "_subDocumentBaseUrlCache", "_objectLoadFailures", "_onloadFired",
                     "_subWindowContainers", "_currentWindowOverride",
                     "_contentDocuments", "_documentContainers",
                 })
        {
            Assert.DoesNotContain(gone, bridgeFields);
        }
    }
}
