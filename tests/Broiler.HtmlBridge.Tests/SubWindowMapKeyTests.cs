using Broiler.Dom;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The sub-window maps' key contract: a frame's window is filed under a handle that is an object,
/// found again from either direction under any handle the realm later produces for that object, and
/// refused outright when it is not an object.
/// </summary>
/// <remarks>
/// <para>
/// <b>Re-typing those maps onto <see cref="JsValue"/> could break exactly one thing silently, and the
/// map it replaced was guarded by the base library rather than by anything in this repository.</b> The
/// reverse map was a dictionary over the engine's object type, so a null key raised
/// <see cref="ArgumentNullException"/>. <see cref="JsValue.Missing"/> is <c>default</c>: a valid key,
/// shared by every absent handle and hashing alike. Without the refusal under test, every sub-window
/// filed from an absent handle would land on ONE reverse entry and the last frame filed would answer
/// for all of them.
/// </para>
/// <para>
/// <b>The refusal throws rather than asserting, because nothing that runs this suite defines
/// <c>DEBUG</c>.</b> <c>eng/Broiler.Configurations.props</c> defines it for the Debug base
/// configuration only, and the three CI jobs that run this project build Release, Release and
/// Release-VM. A <c>Debug.Assert</c> would compile out of all three, and
/// <see cref="ANonObjectIsRefusedBeforeEitherMapIsWritten"/> would pass against a bridge with no
/// guard at all.
/// </para>
/// <para>
/// <b>The windows are minted by a real realm, over every registered engine,</b> because the property
/// under test is only as good as the handles that reach the map. The cache is filed with what
/// <c>NewObject</c> answered and asked with whatever a later read answers for the same object, so one
/// window is stored on a holder and read back before it is looked up: two handles over one object have
/// to be one key. Nothing here names an engine type, so this project's budget does not move.
/// </para>
/// </remarks>
public class SubWindowMapKeyTests
{
    static SubWindowMapKeyTests() => JsEngineHosting.EnsureRegistered();

    /// <summary>Every registered provider, by name, as the conformance suite enumerates them.</summary>
    public static IEnumerable<object[]> Engines =>
        JsEngineRegistry.All.Select(provider => new object[] { provider.Name });

    private static IJsRealm NewRealm(string engine)
    {
        var provider = JsEngineRegistry.Find(engine);
        Assert.NotNull(provider);
        return provider!.CreateRealm(JsRealmOptions.Default);
    }

    /// <summary>A container element, with no bridge and no document load behind it.</summary>
    private static DomElement NewContainer() => new DomDocument().CreateElement("iframe");

    [Theory]
    [MemberData(nameof(Engines))]
    public void AWindowTheRealmMintedIsFoundFromBothSides(string engine)
    {
        using var realm = NewRealm(engine);
        var browsingContexts = new BrowsingContextManager();
        var firstContainer = NewContainer();
        var secondContainer = NewContainer();
        var firstWindow = realm.NewObject();
        var secondWindow = realm.NewObject();

        // Identity first, or every sameness claim below would pass for two absent handles.
        Assert.NotNull(firstWindow.ObjectIdentity);
        Assert.NotNull(secondWindow.ObjectIdentity);
        Assert.NotSame(firstWindow.ObjectIdentity, secondWindow.ObjectIdentity);

        browsingContexts.SetSubWindow(firstContainer, firstWindow);
        browsingContexts.SetSubWindow(secondContainer, secondWindow);

        // Forward: each container answers the handle it was filed with, and not the other one.
        Assert.True(browsingContexts.TryGetSubWindow(firstContainer, out var cachedFirst));
        Assert.True(cachedFirst == firstWindow);
        Assert.True(browsingContexts.TryGetSubWindow(secondContainer, out var cachedSecond));
        Assert.True(cachedSecond == secondWindow);
        Assert.False(cachedFirst == cachedSecond);

        // Reverse, asked with a handle the realm produced LATER for the same object, which is how the
        // window-context switch meets a window: read back off a global, a property, or `this`.
        var holder = realm.NewObject();
        realm.SetProperty(holder, "w", secondWindow);
        var readBack = realm.GetProperty(holder, "w");
        Assert.True(browsingContexts.IsSubWindow(readBack));
        Assert.True(browsingContexts.TryGetSubWindowContainer(readBack, out var container));
        Assert.Same(secondContainer, container);

        // And a window nothing filed is not one.
        var stranger = realm.NewObject();
        Assert.False(browsingContexts.IsSubWindow(stranger));
        Assert.False(browsingContexts.TryGetSubWindowContainer(stranger, out _));
        Assert.Equal(2, browsingContexts.SubWindows.Count());
    }

    [Theory]
    [MemberData(nameof(Engines))]
    public void ANonObjectIsRefusedBeforeEitherMapIsWritten(string engine)
    {
        using var realm = NewRealm(engine);
        var browsingContexts = new BrowsingContextManager();
        var filed = NewContainer();
        var filedWindow = realm.NewObject();
        browsingContexts.SetSubWindow(filed, filedWindow);
        var fresh = NewContainer();

        foreach (var (description, notAnObject) in new (string, JsValue)[]
        {
            ("an absent handle", JsValue.Missing),
            ("undefined", JsValue.Undefined),
            ("null", JsValue.Null),
            ("a string", JsValue.String("window")),
            ("a number", JsValue.Number(0)),
            ("a boolean", JsValue.True),
        })
        {
            // A container that already has a window, and one that has none: neither may be touched.
            foreach (var container in new[] { filed, fresh })
            {
                var refused = Assert.Throws<InvalidOperationException>(
                    () => browsingContexts.SetSubWindow(container, notAnObject));
                Assert.True(
                    refused.Message.Contains("is not an object", StringComparison.Ordinal),
                    $"{description} was refused, but not by the guard under test: {refused.Message}");
            }
        }

        Assert.False(browsingContexts.TryGetSubWindow(fresh, out _));
        Assert.True(browsingContexts.TryGetSubWindow(filed, out var stillFiled));
        Assert.True(stillFiled == filedWindow);
        Assert.False(browsingContexts.IsSubWindow(JsValue.Missing));
        Assert.Single(browsingContexts.SubWindows);
    }

    [Fact]
    public void TwoAbsentHandlesAreOneDictionaryKeyWhichIsWhyTheRefusalExists()
    {
        // The reason the test above is worth its lines, as an assertion rather than a comment: the
        // collapse a tolerated absent handle would produce, one layer down.
        Assert.True(JsValue.Missing == JsValue.Missing);
        Assert.Equal(JsValue.Missing.GetHashCode(), JsValue.Missing.GetHashCode());

        var containers = new Dictionary<JsValue, DomElement>
        {
            [JsValue.Missing] = NewContainer(),
        };
        containers[JsValue.Missing] = NewContainer();

        Assert.Single(containers);
    }
}
