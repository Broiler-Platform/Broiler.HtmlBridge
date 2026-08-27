using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{

    // form.elements.length moved to the Phase 3 FormBinding feature module
    // (Broiler.HtmlBridge.Dom.Features).


    // classList operations delegate to the canonical Broiler.Dom.DomTokenList
    // ordered-set algorithm (parse/serialize on ASCII whitespace, unique-ordered,
    // attribute-synchronized). The bridge keeps only the JavaScript argument
    // marshaling, the lenient empty-token skip these methods have always applied,
    // and the style-scope invalidation callback.
    // classList / DOMTokenList callbacks (contains/add/remove/toggle/replace) moved to the Phase 3
    // ClassListBinding feature module (Broiler.HtmlBridge.Dom.Features).

    // Canvas 2D context callbacks (setFillStyle/…/measureText, formerly JsUtilities…034…058Core) moved to
    // the Phase 3 (P3.64) CanvasBinding feature module (Broiler.HtmlBridge.Dom.Features), unblocked once
    // Phase 6/P8.9 dissolved Broiler.HtmlBridge.Rendering into Dom.

}
