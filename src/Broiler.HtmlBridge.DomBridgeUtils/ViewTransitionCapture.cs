using Broiler.Dom;

namespace Broiler.HtmlBridge;

internal readonly record struct ViewTransitionCapture(
    string Name,
    // The captured element's `view-transition-class` list, matched by a pseudo argument's
    // `.class` part (css-view-transitions-2 <pt-class-selector>). Empty when it has none.
    string Classes,
    double GroupLeft, double GroupTop,
    bool HasOld, double OldLeft, double OldTop, double OldWidth, double OldHeight, string OldBackground, DomElement? OldContent,
    bool HasNew, double NewLeft, double NewTop, double NewWidth, double NewHeight, string NewBackground, DomElement? NewContent);
