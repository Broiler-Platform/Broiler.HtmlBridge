using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The two ways a page asks whether a media type can be played — <c>HTMLMediaElement.canPlayType()</c>
/// (HTML §4.8.11.3) on a <c>&lt;video&gt;</c> or <c>&lt;audio&gt;</c> element, and the static
/// <c>MediaSource.isTypeSupported()</c> (Media Source Extensions §2.3). Pure static, with no host
/// contract: both are capability questions with one answer, and neither reads the document.
/// </summary>
/// <remarks>
/// <para>
/// <b>The answer is "no", and it is not a stub.</b> Broiler's HTML layer has no media playback
/// pipeline wired to it: an element these methods are installed on will not play anything,
/// whatever the type. <c>canPlayType</c>'s vocabulary has a value for exactly that — the empty
/// string, which the specification defines as "the user agent is confident the type cannot be
/// rendered" — and <c>isTypeSupported</c>'s is <c>false</c>. Answering that way is the honest
/// report of the engine's current capability, and it is the answer a player needs in order to fall
/// back rather than wait on a video that will never arrive.
/// </para>
/// <para>
/// What was there before was not "no" but a crash: <c>video.canPlayType(type)</c> was a TypeError
/// ("undefined is not a function") and <c>MediaSource</c> a ReferenceError, and both abort the
/// function that asked. Format probing is the first thing a media player does, so the abort took
/// out the code that would have chosen a fallback along with the code that would have chosen a
/// codec.
/// </para>
/// <para>
/// When playback is wired up, this is the one place the answer changes:
/// <c>Broiler.Playback.MediaPlayer.CanPlayType</c> already answers the same question from a
/// <c>MediaCodecCatalog</c>, and the catalog is supplied by whichever host composes the engine. Both
/// methods below then read from that instead of from <see cref="NotSupported"/> — the interface a
/// page sees does not change.
/// </para>
/// </remarks>
internal static class MediaCapabilityBinding
{
    /// <summary>
    /// <c>canPlayType</c>'s "cannot be rendered" answer. The empty string is the specified value,
    /// not a missing one — the other two are <c>"maybe"</c> and <c>"probably"</c>.
    /// </summary>
    private static readonly JSString NotSupported = new(string.Empty);

    /// <summary>
    /// Installs the <c>HTMLMediaElement</c> capability member on <paramref name="obj"/>. Called for
    /// every element, so it does nothing unless this one is a media element: <c>canPlayType</c>
    /// belongs to that interface, and <c>'canPlayType' in el</c> must not be true of a
    /// <c>&lt;div&gt;</c>.
    /// </summary>
    /// <param name="obj">The element's JS wrapper.</param>
    /// <param name="tag">The element's lowercased tag name.</param>
    public static void Install(JSObject obj, string tag)
    {
        if (tag is not ("video" or "audio"))
            return;

        obj.FastAddValue("canPlayType",
            new DomFunction((in _) => NotSupported, "canPlayType", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>
    /// Builds the <c>MediaSource</c> interface object. A <see cref="JSFunction"/> rather than a
    /// <c>DomFunction</c> because <c>new MediaSource()</c> is how the interface is normally
    /// reached — and constructing one succeeds, because a source that supports no type is still a
    /// source; a page learns what it can do from <c>isTypeSupported</c> and from
    /// <c>addSourceBuffer</c> refusing the type it was given.
    /// </summary>
    /// <param name="context">
    /// The realm whose <c>DOMException</c> constructor <c>addSourceBuffer</c> raises its
    /// <c>NotSupportedError</c> through, so a page's <c>catch</c> sees the same exception type a
    /// browser would give it.
    /// </param>
    public static JSFunction BuildMediaSource(JSContext context)
    {
        var constructor = new JSFunction((in _) => NewMediaSource(context), "MediaSource", 0);

        constructor.FastAddValue("isTypeSupported",
            new DomFunction((in _) => JSBoolean.False, "isTypeSupported", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return constructor;
    }

    private static JSValue NewMediaSource(JSContext context)
    {
        var source = new JSObject();

        // "closed" is the state a MediaSource is in until it is attached to a media element. It is
        // the one this never leaves, because there is no element for it to be attached to.
        source.FastAddValue("readyState", new JSString("closed"), JSPropertyAttributes.EnumerableConfigurableValue);

        // The buffer lists are empty and stay empty: addSourceBuffer refuses every type, which is
        // what the specification requires of a type isTypeSupported rejects.
        source.FastAddValue("sourceBuffers", new JSArray(), JSPropertyAttributes.EnumerableConfigurableValue);
        source.FastAddValue("activeSourceBuffers", new JSArray(), JSPropertyAttributes.EnumerableConfigurableValue);

        source.FastAddValue("addSourceBuffer",
            new DomFunction((in a) => AddSourceBuffer(context, in a), "addSourceBuffer", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return source;
    }

    private static JSValue AddSourceBuffer(JSContext context, in Arguments a)
    {
        string type = a.Length > 0 ? a[0].ToString() : string.Empty;
        DomBridge.ThrowDOMException(
            context,
            $"The type '{type}' is not supported: no media playback pipeline is available.",
            "NotSupportedError");
        return JSUndefined.Value;
    }
}
