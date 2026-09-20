using System.Collections.Generic;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="StyleDeclarationBinding"/>'s inline (<c>element.style</c>)
/// declaration callbacks need from the bridge: the authoritative per-element inline-style dictionary
/// and the "set via JS" bookkeeping. These read/write the per-bridge inline-style runtime state, so
/// they are bridge-instance methods rather than neutral
/// <c>internal static</c> helpers. The stateless helpers the module also uses
/// (<c>IsAcceptableInlineValue</c>, <c>ParseStyle</c>, <c>ExpandCssShorthands</c>, and the
/// <c>CssPropertyNames</c> table) stay static and are called directly.
/// </summary>
internal interface IInlineStyleHost
{
    Dictionary<string, string> InlineStyle(DomElement element);
    void MarkInlineStylePropSetByJs(DomElement element, string property);
    void UnmarkInlineStylePropSetByJs(DomElement element, string property);
    void ClearInlineStylePropsSetByJs(DomElement element);
    IReadOnlyCollection<string> InlineStylePropsSetByJs(DomElement element);
}
