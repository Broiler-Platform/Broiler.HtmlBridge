using Broiler.HtmlBridge.Jseal;

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
/// methods below then read from that instead of from <see cref="NotSupportedType"/> — the interface a
/// page sees does not change.
/// </para>
/// <para>
/// Both halves take the realm their members are minted in — <see cref="Install"/> from the
/// element-interface hub, <see cref="BuildMediaSource"/> from
/// <c>DomBridge/Registration/Polyfills.cs</c> — so this file names no engine type. The
/// <c>NotSupportedError</c> that <c>addSourceBuffer</c> raises goes through the call's own realm,
/// which builds it against the same <c>DOMException</c> global the script context did.
/// </para>
/// </remarks>
internal static class MediaCapabilityBinding
{
    /// <summary>
    /// <c>canPlayType</c>'s "cannot be rendered" answer. The empty string is the specified value,
    /// not a missing one — the other two are <c>"maybe"</c> and <c>"probably"</c>.
    /// </summary>
    private const string NotSupportedType = "";

    /// <summary>
    /// Installs the <c>HTMLMediaElement</c> capability member on <paramref name="obj"/>. Called for
    /// every element, so it does nothing unless this one is a media element: <c>canPlayType</c>
    /// belongs to that interface, and <c>'canPlayType' in el</c> must not be true of a
    /// <c>&lt;div&gt;</c>.
    /// </summary>
    /// <param name="realm">The realm the member is minted in.</param>
    /// <param name="obj">The element's JS wrapper.</param>
    /// <param name="tag">The element's lowercased tag name.</param>
    public static void Install(IJsRealm realm, JsValue obj, string tag)
    {
        if (tag is not ("video" or "audio"))
            return;

        realm.DefineValue(obj, "canPlayType",
            realm.NewMethod("canPlayType", static (in _) => JsValue.String(NotSupportedType), 1));
    }

    /// <summary>
    /// Builds the <c>MediaSource</c> interface object. A constructor rather than a plain method
    /// because <c>new MediaSource()</c> is how the interface is normally reached — and constructing
    /// one succeeds, because a source that supports no type is still a source; a page learns what it
    /// can do from <c>isTypeSupported</c> and from <c>addSourceBuffer</c> refusing the type it was
    /// given.
    /// </summary>
    /// <param name="realm">The realm the interface object and each source it builds are minted in.</param>
    public static JsValue BuildMediaSource(IJsRealm realm)
    {
        var constructor = realm.NewConstructor("MediaSource", (in _) => NewMediaSource(realm), 0);

        realm.DefineValue(constructor, "isTypeSupported",
            realm.NewMethod("isTypeSupported", static (in _) => JsValue.False, 1));

        return constructor;
    }

    private static JsValue NewMediaSource(IJsRealm realm)
    {
        var source = realm.NewObject();

        // "closed" is the state a MediaSource is in until it is attached to a media element. It is
        // the one this never leaves, because there is no element for it to be attached to.
        realm.DefineValue(source, "readyState", JsValue.String("closed"));

        // The buffer lists are empty and stay empty: addSourceBuffer refuses every type, which is
        // what the specification requires of a type isTypeSupported rejects.
        realm.DefineValue(source, "sourceBuffers", realm.NewArray());
        realm.DefineValue(source, "activeSourceBuffers", realm.NewArray());

        realm.DefineValue(source, "addSourceBuffer",
            realm.NewMethod("addSourceBuffer", (in call) => AddSourceBuffer(in call), 1));

        return source;
    }

    private static JsValue AddSourceBuffer(in JsCall call)
    {
        string type = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        throw call.Realm.DomError(
            "NotSupportedError",
            $"The type '{type}' is not supported: no media playback pipeline is available.");
    }
}
