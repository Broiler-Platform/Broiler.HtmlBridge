using Broiler.HtmlBridge.Dom.Features;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IFetchHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.FetchBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.11). The single member is an explicit
/// interface implementation, so it does not widen the public <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : IFetchHost
{
    /// <summary>
    /// The entry list of a <c>&lt;form&gt;</c> wrapper, or <see langword="null"/> for anything else —
    /// what <c>new FormData(form)</c> collects.
    /// </summary>
    IReadOnlyList<KeyValuePair<string, string>>? IFetchHost.FormEntriesFor(
        Broiler.JavaScript.Runtime.JSObject candidate) =>
        FindDomNodeByJSObject(candidate) is Broiler.Dom.DomElement element &&
        string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase)
            ? BuildFormEntryList(element)
            : null;

    string IFetchHost.PageUrl => _pageUrl;

    Broiler.JavaScript.Runtime.JSValue IFetchHost.StreamOverText(string text) =>
        _streams.StreamOverText(_jsContext!, text);

    Broiler.JavaScript.Runtime.JSValue IFetchHost.StreamOverTextObserved(string text, System.Action onDisturbed) =>
        _streams.StreamOverTextObserved(_jsContext!, text, onDisturbed);

    bool IFetchHost.IsStreamLocked(Broiler.JavaScript.Runtime.JSValue stream) => _streams.IsStreamLocked(stream);

    Broiler.JavaScript.Runtime.JSValue IFetchHost.CreateBlob(byte[] bytes, string contentType) =>
        _blobs.CreateBlobFromBytes(bytes, contentType);
}
