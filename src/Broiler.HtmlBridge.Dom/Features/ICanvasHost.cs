using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// What <see cref="CanvasBinding"/> needs from the bridge: the window object, so that
/// <c>getImageData</c>/<c>createImageData</c> can build their <c>data</c> array with the realm's own
/// <c>Uint8ClampedArray</c> constructor rather than a look-alike.
/// </summary>
/// <remarks>
/// A typed array's prototype comes from the constructor it was called through, so one built directly in
/// C# would carry the wrong prototype and answer <c>false</c> to <c>instanceof Uint8ClampedArray</c> —
/// which is exactly the check a page runs before trusting an <c>ImageData</c>. Going through the global
/// is what makes the result a real typed array; <c>FetchBinding</c>'s <c>Uint8Array</c> construction
/// takes the same route.
/// </remarks>
internal interface ICanvasHost
{
    JSObject? WindowJSObject { get; }
}
