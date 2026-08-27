using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The narrow surface <see cref="WindowContextManager"/> needs from the bridge: the top-level window and
/// document JS objects, the JS-context global read/write and eval used by the window-context switch, and
/// the sub-document builder a target window resolves its document through. Implemented by <c>DomBridge</c>
/// via explicit interface members (see <c>DomBridge.WindowContextHost.cs</c>). The sub-window identity and
/// owner-window state are not part of this contract — the manager reads them from the
/// <see cref="BrowsingContextManager"/> and <see cref="EventTargetRegistry"/> it holds directly.
/// </summary>
internal interface IWindowContextHost
{
    /// <summary>The top-level window JS object (the identity a candidate is canonicalised against, and the
    /// <c>top</c> the context switch restores to).</summary>
    JSObject? WindowJSObject { get; }

    /// <summary>The top-level document JS object, or <c>undefined</c> when absent.</summary>
    JSValue MainDocumentOrUndefined { get; }

    /// <summary>Whether a JS context is attached (when not, <c>RunWithWindowContext</c> just runs the
    /// callback).</summary>
    bool HasJsContext { get; }

    /// <summary>Evaluates an expression in the JS context (used to snapshot the globals the context switch
    /// saves/restores), or <c>null</c> when there is no context.</summary>
    JSValue? Eval(string expression);

    /// <summary>Reads a JS global by name (e.g. the current <c>window</c>), or <c>null</c>.</summary>
    JSValue? GetGlobal(string name);

    /// <summary>Sets a JS global by name (the window-context switch and its restore).</summary>
    void SetGlobal(string name, JSValue value);

    /// <summary>The sub-document JS object for a container (a target window's <c>document</c>).</summary>
    JSObject GetOrCreateSubDocument(DomElement container);
}
