using System.Collections.Generic;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

// Explicit IInlineStyleHost implementation for StyleDeclarationBinding's inline (element.style)
// declaration callbacks (Phase 2 item 4 de-globalization, 2026-07-17): the per-element inline-style
// dictionary and "set via JS" bookkeeping moved off the process-static ElementRuntimeState table onto
// the bridge instance, so the module reaches them through this narrow contract (each member forwards to
// the corresponding bridge-instance helper) rather than a static DomBridge call.
public sealed partial class DomBridge : Dom.Features.IInlineStyleHost
{
    Dictionary<string, string> Dom.Features.IInlineStyleHost.InlineStyle(DomElement element)
        => InlineStyle(element);

    void Dom.Features.IInlineStyleHost.MarkInlineStylePropSetByJs(DomElement element, string property)
        => MarkInlineStylePropSetByJs(element, property);

    void Dom.Features.IInlineStyleHost.UnmarkInlineStylePropSetByJs(DomElement element, string property)
        => UnmarkInlineStylePropSetByJs(element, property);

    void Dom.Features.IInlineStyleHost.ClearInlineStylePropsSetByJs(DomElement element)
        => ClearInlineStylePropsSetByJs(element);

    IReadOnlyCollection<string> Dom.Features.IInlineStyleHost.InlineStylePropsSetByJs(DomElement element)
        => InlineStylePropsSetByJs(element);
}
