using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IFetchHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.FetchBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.11). Every member is an explicit interface
/// implementation, so none of them widens the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// <b>There was never an engine reference here, and the sentence that used to stand in this place
/// counted wrong.</b> It said one was left and that the wrapper registry pinned it. What was left was
/// a <c>JsInterop</c> crossing, and a crossing names no engine type: <c>eng/jseal-budget.json</c>
/// counts the engine's namespace as text, and this file has never contributed a single occurrence of
/// it. The crossing is gone too — the wrapper-to-node lookup takes the handle now, and the registry
/// behind it is keyed on <see cref="JsValue.ObjectIdentity"/> rather than on an engine object — and
/// nothing in <c>FetchBinding</c> changed when it went.
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
    /// The <c>IsObject</c> test and the lookup answer the same question now: a handle that is not an
    /// object is not in the wrapper map either. The test is kept because collapsing the ten redundant
    /// guards this re-typing left across the assembly is a separate change, not because a primitive
    /// would reach anything that minds.
    /// </remarks>
    IReadOnlyList<KeyValuePair<string, string>>? IFetchHost.FormEntriesFor(JsValue candidate) =>
        candidate.IsObject &&
        FindDomNodeByJSObject(candidate) is Broiler.Dom.DomElement element &&
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
