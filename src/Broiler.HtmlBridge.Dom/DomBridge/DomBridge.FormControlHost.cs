using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IFormControlHost implementation for the FormControlBinding feature module (Phase 3): the
// input's dirty IDL value/checked state stays on the per-element FormControl runtime slot and is exposed
// here as named primitives; the <select> value resolution delegates to the SelectBinding the bridge
// owns, and the radio-sibling walk / style-scope invalidation forward to the existing bridge helpers.
// Explicit interface members, so these seams do not widen the public DomBridge surface.
//
// This is the half-migrated seam for the form-control slice: the module speaks JSEAL, and the one
// member that produces a JavaScript object — the FileList — is still built by DomCollectionBinding,
// which is not migrated. So the engine object is minted on this side and crosses through JsInterop,
// which carries it without converting it; the module never sees an engine type.
public sealed partial class DomBridge : Dom.Features.IFormControlHost
{
    /// <summary>One <c>FileList</c> per file input, cached so <c>input.files === input.files</c>. The
    /// contents function stays live over an always-empty list rather than being a fixed one, so a file
    /// selection would need no second shape.</summary>
    private readonly Dictionary<DomElement, JsValue> _fileLists = [];

    IJsRealm Dom.Features.IFormControlHost.Realm => Realm;

    JsValue Dom.Features.IFormControlHost.GetFileList(DomElement element)
    {
        if (_fileLists.TryGetValue(element, out var existing))
            return existing;

        var files = JsInterop.FromEngineObject(
            (Broiler.JavaScript.Runtime.JSObject)Dom.Features.DomCollectionBinding.FileList(_jsContext, static () => []));
        _fileLists[element] = files;
        return files;
    }

    bool Dom.Features.IFormControlHost.TryGetFormControlValue(DomElement element, out string value)
    {
        if (FormControlStateFor(element).Value.TryGet(out var stored) && stored is string s)
        {
            value = s;
            return true;
        }

        value = string.Empty;
        return false;
    }

    void Dom.Features.IFormControlHost.SetFormControlValue(DomElement element, string value) =>
        FormControlStateFor(element).Value.Set(value);

    string Dom.Features.IFormControlHost.GetSelectValue(DomElement element) => _select.GetValue(element);

    void Dom.Features.IFormControlHost.SetSelectValue(DomElement element, string value) =>
        _select.SetValue(element, value);

    bool Dom.Features.IFormControlHost.TryGetFormControlChecked(DomElement element, out bool value)
    {
        if (FormControlStateFor(element).Checked.TryGet(out var stored))
        {
            value = stored is true;
            return true;
        }

        value = false;
        return false;
    }

    void Dom.Features.IFormControlHost.SetFormControlChecked(DomElement element, bool value) =>
        FormControlStateFor(element).Checked.Set(value);

    void Dom.Features.IFormControlHost.UncheckRadioSiblings(DomElement scope, DomElement except, string radioName) =>
        UncheckRadioSiblings(scope, except, radioName);

    void Dom.Features.IFormControlHost.InvalidateStyleScope(DomElement anchor) => InvalidateStyleScope(anchor);

    void Dom.Features.IFormControlHost.SetElementTextContent(DomElement element, string value) =>
        SetElementTextContent(element, value);
}
