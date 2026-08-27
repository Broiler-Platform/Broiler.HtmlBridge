using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IWindowContextHost"/>, the contract the
/// <see cref="WindowContextManager"/> owner consumes (HtmlBridge complexity-reduction roadmap Phase 3,
/// P3.18). Explicit interface members, so these JS-context seams do not widen the public
/// <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : IWindowContextHost
{
    JSObject? IWindowContextHost.WindowJSObject => _windowJSObject;

    JSValue IWindowContextHost.MainDocumentOrUndefined => _documentJSObject ?? JSUndefined.Value;

    bool IWindowContextHost.HasJsContext => _jsContext != null;

    JSValue? IWindowContextHost.Eval(string expression) => _jsContext?.Eval(expression);

    JSValue? IWindowContextHost.GetGlobal(string name) => _jsContext?[name];

    void IWindowContextHost.SetGlobal(string name, JSValue value)
    {
        if (_jsContext != null)
            _jsContext[name] = value;
    }

    JSObject IWindowContextHost.GetOrCreateSubDocument(DomElement container) => GetOrCreateSubDocument(container);
}
