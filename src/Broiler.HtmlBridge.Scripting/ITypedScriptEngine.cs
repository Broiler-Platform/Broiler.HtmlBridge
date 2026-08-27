namespace Broiler.HtmlBridge;

/// <summary>
/// Optional typed extension to <see cref="IScriptEngine"/> for consumers that
/// can render the canonical document directly.
/// </summary>
public interface ITypedScriptEngine : IScriptEngine
{
    Broiler.Dom.DomDocument? ExecuteToDocument(
        IReadOnlyList<string> scripts,
        IReadOnlyList<string> deferredScripts,
        string html,
        string? url);
}
