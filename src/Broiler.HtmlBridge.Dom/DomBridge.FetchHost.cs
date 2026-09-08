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
/// <b>This file is where the fetch surface's engine types stop.</b> The contract itself speaks
/// <see cref="JsValue"/>, but three of the things it forwards to have not migrated — the streams
/// module's four members, which still take and return the engine's own values, and the bridge's
/// wrapper-to-node lookup — so the conversions through <see cref="JsInterop"/> are gathered here
/// rather than spread through the binding. They go when <c>Features/StreamsBinding.cs</c> and the
/// bridge's wrapper registry migrate; nothing in <c>FetchBinding</c> changes when they do.
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

    JsValue IFetchHost.StreamOverText(string text) =>
        FetchStreamHandle(_streams.StreamOverText(_jsContext!, text));

    JsValue IFetchHost.StreamOverTextObserved(string text, System.Action onDisturbed) =>
        FetchStreamHandle(_streams.StreamOverTextObserved(_jsContext!, text, onDisturbed));

    bool IFetchHost.IsStreamLocked(JsValue stream) =>
        stream.IsObject && _streams.IsStreamLocked(JsInterop.ToEngineObject(stream));

    JsValue IFetchHost.CreateBlob(byte[] bytes, string contentType) =>
        _blobs.CreateBlobFromBytes(Realm, bytes, contentType);

    /// <summary>
    /// A stream the unmigrated streams module produced, as a handle.
    /// </summary>
    /// <remarks>
    /// The module answers the engine's <c>null</c> singleton when it has no factory to build a stream
    /// with — a realm whose streams asset did not evaluate — and <see cref="JsInterop"/> converts an
    /// object and nothing else, so that arm is spelled out rather than passed through.
    /// </remarks>
    private static JsValue FetchStreamHandle(Broiler.JavaScript.Runtime.JSValue value) =>
        value is Broiler.JavaScript.Runtime.JSObject instance
            ? JsInterop.FromEngineObject(instance)
            : JsValue.Null;
}
