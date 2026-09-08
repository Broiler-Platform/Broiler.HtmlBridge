using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IFetchHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.FetchBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.11). Every member is an explicit interface
/// implementation, so none of them widens the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// <b>One engine reference is left and the bridge's wrapper registry pins it.</b> The contract
/// speaks <see cref="JsValue"/> throughout, and so does everything it forwards to — the streams
/// module included — except the wrapper-to-node lookup, which is keyed on the engine object a handle
/// carries and is called that way by fifteen files across the assembly. So the one unwrap through
/// <see cref="JsInterop"/> below goes when that registry does; nothing in <c>FetchBinding</c> changes
/// when it happens.
/// </remarks>
public sealed partial class DomBridge : IFetchHost
{
    /// <inheritdoc />
    IJsRealm IFetchHost.Realm => Realm;

    /// <summary>
    /// The entry list of a <c>&lt;form&gt;</c> wrapper, or <see langword="null"/> for anything else —
    /// what <c>new FormData(form)</c> collects.
    /// </summary>
    /// <remarks>
    /// Only an object can be a wrapper, so a primitive is refused here rather than in the lookup,
    /// which keys on wrapper identity and would have nothing to look up.
    /// </remarks>
    IReadOnlyList<KeyValuePair<string, string>>? IFetchHost.FormEntriesFor(JsValue candidate) =>
        candidate.IsObject &&
        FindDomNodeByJSObject(JsInterop.ToEngineObject(candidate)) is Broiler.Dom.DomElement element &&
        string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase)
            ? BuildFormEntryList(element)
            : null;

    string IFetchHost.PageUrl => _pageUrl;

    JsValue IFetchHost.StreamOverText(string text) => _streams.StreamOverText(text);

    JsValue IFetchHost.StreamOverTextObserved(string text, System.Action onDisturbed) =>
        _streams.StreamOverTextObserved(text, onDisturbed);

    bool IFetchHost.IsStreamLocked(JsValue stream) => _streams.IsStreamLocked(stream);

    JsValue IFetchHost.CreateBlob(byte[] bytes, string contentType) =>
        _blobs.CreateBlobFromBytes(Realm, bytes, contentType);
}
