using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Jseal.Providers;
using Broiler.JavaScript.Runtime;
using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>JsInterop</c> is the cast between an engine object and a JSEAL handle that the DOM bridge still
/// makes where the contract lacks an operation, and the one property it owes is that a handle
/// crossing it is indistinguishable from one the provider made.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one test file in this project that names an engine type, and the budget entry for
/// this directory anticipated it.</b> <c>eng/jseal-budget.json</c> says of
/// <c>Broiler.Browser.Core.Tests</c>: "if a test ever needs to name one directly, that is a real
/// budget increase and this file is where the argument for it belongs." The argument is that the
/// seam's whole subject is the correspondence between an engine object and a JSEAL handle, so a test
/// that could not name the first half could not assert the correspondence — it could only assert
/// that JSEAL agrees with itself. The increase is one reference, a using line, and it buys the only
/// check that would catch the defect below coming back. (This said two references.)
/// </para>
/// <para>
/// <b>What it pins.</b> <c>JsProviderValue.Object</c> is documented as the wrapper for an engine
/// object that is neither callable nor an Array exotic, and <c>FromEngineObject</c> called it for
/// everything — so a function crossing the seam came back kind <c>Object</c> while the same function
/// from <c>BroilerJsMarshal.Wrap</c> came back kind <c>Function</c>. <see cref="JsValue"/> compares
/// kind before reference, so the two handles were unequal despite naming one object. This said the
/// defect stayed latent only until a listener record held a handle. The record has held one since
/// cb0ecb2, and a listener is a list element, never a map key. A map that does key on function
/// handles, <c>CustomElementsBinding</c>'s constructor map, files and finds them with handles
/// straight from call frames the provider filled, and the one bridge call to
/// <c>FromEngineObject</c> mints an array.
/// </para>
/// </remarks>
public class JsInteropSeamTests
{
    static JsInteropSeamTests() => Broiler.HtmlBridge.JsEngineHosting.EnsureRegistered();

    private static IJsRealm NewBroilerJsRealm() =>
        JsEngineRegistry.Find("broiler-js")!.CreateRealm(JsRealmOptions.Default);

    /// <summary>
    /// A function, an array and a plain object each keep their kind across the seam.
    /// </summary>
    /// <remarks>
    /// The values are made by evaluating host script rather than by minting them here, so the
    /// handles on the left are exactly what the provider produces for a page's own values — which is
    /// the thing the seam has to agree with.
    /// </remarks>
    [Fact]
    public void AHandleCrossingTheSeamKeepsTheKindTheProviderGaveIt()
    {
        using var realm = NewBroilerJsRealm();

        foreach (var (source, expected) in new (string, JsValueKind)[]
        {
            ("(function () {})", JsValueKind.Function),
            ("[1, 2, 3]", JsValueKind.Array),
            ("({ a: 1 })", JsValueKind.Object),
        })
        {
            var fromProvider = realm.EvaluateHostScript(source, "test:seam");
            Assert.Equal(expected, fromProvider.Kind);

            // Down to the engine and back across the cast.
            var engineObject = (JSObject)JsProviderValue.ReferenceOf(fromProvider)!;
            var roundTripped = JsInterop.FromEngineObject(engineObject);

            Assert.Equal(expected, roundTripped.Kind);
        }
    }

    /// <summary>
    /// Two handles over one object are equal, whichever side minted them.
    /// </summary>
    /// <remarks>
    /// This is the assertion the defect actually broke, and it is stated separately from the kind
    /// because equality is what the bridge's handle-keyed maps depend on — a map that stored a
    /// provider-made key and looked it up with a seam-made one missed every time.
    /// </remarks>
    [Fact]
    public void AProviderHandleAndASeamHandleOverOneObjectCompareEqual()
    {
        using var realm = NewBroilerJsRealm();

        var fromProvider = realm.EvaluateHostScript("(function () {})", "test:seam-equality");
        var roundTripped = JsInterop.FromEngineObject((JSObject)JsProviderValue.ReferenceOf(fromProvider)!);

        Assert.True(fromProvider == roundTripped);
        Assert.Equal(fromProvider.GetHashCode(), roundTripped.GetHashCode());

        // And the same handle used as a dictionary key finds its own entry, which is the shape the
        // bridge's target and constructor maps are; a listener sits in a list, not as a key.
        var map = new System.Collections.Generic.Dictionary<JsValue, string> { [fromProvider] = "listener" };
        Assert.True(map.TryGetValue(roundTripped, out var found));
        Assert.Equal("listener", found);
    }
}
