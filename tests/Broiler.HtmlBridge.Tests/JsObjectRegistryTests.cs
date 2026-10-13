using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Direct unit tests for the wrapper-identity authority (<see cref="JsObjectRegistry"/>). That a
/// node keeps one wrapper however page script reaches it is asserted from script by
/// <c>WrapperIdentityTests</c>; these pin the registry the bridge answers it from.
/// </summary>
public class JsObjectRegistryTests
{
    [Fact]
    public void AWrapperSetForANodeIsTheOneFoundForIt()
    {
        using var realm = TestRealm.Create();
        var node = new DomDocument().CreateElement("div");
        var wrapper = realm.NewObject();
        var registry = new JsObjectRegistry();

        registry.Set(node, wrapper);

        Assert.True(registry.TryGet(node, out var found));
        Assert.True(found == wrapper);
    }

    [Fact]
    public void WrappersAreKeyedByTheNodeNotByItsValue()
    {
        using var realm = TestRealm.Create();
        var document = new DomDocument();
        var a = document.CreateElement("div");
        var b = document.CreateElement("div"); // structurally identical, a distinct node
        var registry = new JsObjectRegistry();

        registry.Set(a, realm.NewObject());

        Assert.True(registry.TryGet(a, out _));
        Assert.False(registry.TryGet(b, out _));
    }

    [Fact]
    public void RemoveDropsTheWrapper()
    {
        using var realm = TestRealm.Create();
        var node = new DomDocument().CreateElement("div");
        var registry = new JsObjectRegistry();
        registry.Set(node, realm.NewObject());

        Assert.True(registry.Remove(node));
        Assert.False(registry.TryGet(node, out _));
        Assert.False(registry.Remove(node)); // idempotent
    }

    [Fact]
    public void ANodeIsFoundFromItsWrapper()
    {
        using var realm = TestRealm.Create();
        var node = new DomDocument().CreateElement("div");
        var wrapper = realm.NewObject();
        var registry = new JsObjectRegistry();
        registry.Set(node, wrapper);

        Assert.True(registry.TryGetNode(wrapper, out var found));
        Assert.Same(node, found);
        Assert.False(registry.TryGetNode(realm.NewObject(), out _));
        // A receiver that is not an object is simply not in the map.
        Assert.False(registry.TryGetNode(JsValue.Number(1), out _));
    }

    [Fact]
    public void AWrapperThatIsNotAnObjectIsRefused()
    {
        var node = new DomDocument().CreateElement("div");

        Assert.Throws<InvalidOperationException>(() => new JsObjectRegistry().Set(node, JsValue.Undefined));
    }

    [Fact]
    public void DocumentWrappersAreASeparateMap()
    {
        using var realm = TestRealm.Create();
        var root = new DomDocument().CreateElement("div");
        var documentWrapper = realm.NewObject();
        var registry = new JsObjectRegistry();

        registry.SetDocument(root, documentWrapper);

        Assert.True(registry.TryGetDocument(root, out var found));
        Assert.True(found == documentWrapper);
        Assert.False(registry.TryGet(root, out _)); // independent of the node map
    }

    [Fact]
    public void ClearDropsNodeAndDocumentWrappers()
    {
        using var realm = TestRealm.Create();
        var document = new DomDocument();
        var node = document.CreateElement("div");
        var root = document.CreateElement("div");
        var registry = new JsObjectRegistry();
        registry.Set(node, realm.NewObject());
        registry.SetDocument(root, realm.NewObject());

        registry.Clear();

        Assert.False(registry.TryGet(node, out _));
        Assert.False(registry.TryGetDocument(root, out _));
    }
}
