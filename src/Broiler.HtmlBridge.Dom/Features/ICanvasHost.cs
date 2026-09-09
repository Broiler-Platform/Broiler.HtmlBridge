using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// What <see cref="CanvasBinding"/> needs from the bridge: the window object, so that
/// <c>getImageData</c>/<c>createImageData</c> can build their <c>data</c> array with the realm's own
/// <c>Uint8ClampedArray</c> constructor rather than a look-alike.
/// </summary>
/// <remarks>
/// <para>
/// A typed array's prototype comes from the constructor it was called through, so one built directly in
/// C# would carry the wrong prototype and answer <c>false</c> to <c>instanceof Uint8ClampedArray</c> —
/// which is exactly the check a page runs before trusting an <c>ImageData</c>. Going through the global
/// is what makes the result a real typed array; <c>FetchBinding</c>'s <c>Uint8Array</c> construction
/// takes the same route.
/// </para>
/// <para>
/// <b>Why the window and not <see cref="IJsRealm.Global"/>.</b> The bridge's <c>window</c> is the object
/// registration built and populated, and it is the one the page's own <c>Uint8ClampedArray</c> is looked
/// up on today. Asking the realm for <c>globalThis</c> instead would be a different object on any engine
/// that separates the two, so the contract keeps naming the window and the answer is unchanged.
/// </para>
/// </remarks>
internal interface ICanvasHost
{
    /// <summary>
    /// The page's <c>window</c>, or a non-object when the bridge has not built one yet — the caller
    /// checks, because a canvas can be wrapped before registration has run.
    /// </summary>
    JsValue Window { get; }
}
