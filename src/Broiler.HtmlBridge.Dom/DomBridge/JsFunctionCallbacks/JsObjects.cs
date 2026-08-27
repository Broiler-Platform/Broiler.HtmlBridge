using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Logging;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{

    // HTMLElement global content-attribute reflectors (id, className, title, lang, accessKey, dir,
    // draggable) moved to the GlobalAttributeBinding feature module (Phase 3 P3.54).

    // innerHTML / outerHTML / textContent get+set moved to the ElementContentBinding feature module
    // (Phase 3 P3.57).

    // element.shadowRoot getter moved to the ShadowDomBinding feature module (Phase 3 P3.62).

    // element.style = "..." cssText assignment setter moved to StyleDeclarationBinding (Phase 3 P3.63).

    // insertBefore(newChild, refChild) moved to the TreeMutationBinding feature module (Phase 3 P3.58).

    // attachShadow(init) moved to the ShadowDomBinding feature module (Phase 3 P3.62).

    // appendChild / append / prepend / removeChild / replaceChild moved to the TreeMutationBinding
    // feature module (Phase 3 P3.58).

    // get/set on<event> inline event-handler reflectors moved to the EventHandlerReflectorBinding
    // feature module (Phase 3 P3.59).

    // Form-control IDL reflectors (value/checked/type/name/disabled/hidden/tabIndex/required) moved to
    // the FormControlBinding feature module (Phase 3 P3.60).

    // form.submit() moved to the FormSubmitBinding feature module (Phase 3 P3.61).

    // insertAdjacentElement / insertAdjacentText / insertAdjacentHTML (and their
    // NormalizeInsertAdjacentPosition / GetInsertAdjacentTarget helpers) moved to the
    // InsertAdjacentBinding feature module (Phase 3 P3.56).

    // canvas.getContext("2d") (and its BuildCanvas2DContext + JsUtilities…034…058Core drawing callbacks)
    // moved to the CanvasBinding feature module (Phase 3 P3.64) — the last element-member callback in the
    // mixed JsObjects.cs file, unblocked once Phase 6/P8.9 dissolved Broiler.HtmlBridge.Rendering into Dom.

    // <iframe> browsing-context accessors (contentDocument/contentWindow/getSVGDocument, src/srcdoc
    // setters) moved to the IframeElementBinding feature module (Phase 3 P3.55).

}
