using Broiler.Dom;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The narrow surface <see cref="ScriptInsertionRunner"/> needs from the bridge to run a
/// script-inserted <c>&lt;script&gt;</c>: the document it watches, the page URL and policy a
/// candidate is authorised against, the realm it evaluates in, the event-loop queue it defers
/// an external script's fetch to, and the element event it fires when that fetch settles.
/// Implemented by <c>DomBridge</c> via explicit interface members (see
/// <c>DomBridge.ScriptInsertionHost.cs</c>).
/// </summary>
internal interface IScriptInsertionHost
{
    /// <summary>The document whose insertions are watched — the runner ignores any script whose
    /// root is something else (a detached subtree, or another document's tree).</summary>
    DomDocument Document { get; }

    /// <summary>Whether a JavaScript realm is attached. Nothing can run before <c>Attach</c>.</summary>
    bool HasRealm { get; }

    /// <summary>The document URL, for resolving a relative <c>src</c> and as the CSP self-origin.</summary>
    string PageUrl { get; }

    /// <summary>The policy a candidate is authorised against, when the document declares one.</summary>
    ContentSecurityPolicy? Csp { get; }

    /// <summary>
    /// True while the bridge is mutating the live tree itself (serialize/render bake, re-parse).
    /// Those insertions are an implementation detail and must not run script — the same guard
    /// <c>MutationObserver</c> delivery uses.
    /// </summary>
    bool MutationDeliverySuppressed { get; }

    /// <summary>Queues work as a due-now task on the event loop, taking its turn in registration order
    /// among the page's timers (a script's deferred fetch+run).</summary>
    void QueueTask(Action task);

    /// <summary>Evaluates classic script source on the page's behalf, under
    /// <paramref name="label"/> — the name errors from it are reported against.</summary>
    /// <remarks>
    /// A CLASSIC SCRIPT in the specification's sense, which is what decides how the bridge evaluates
    /// it: <c>script-src</c> governs a script element and the caller has already taken that decision,
    /// so the evaluation below is unconditional. It is not this repository's script, and it is not
    /// the page asking to evaluate a string at run time either — those are the other two thirds of
    /// <c>IJsSource</c>.
    /// </remarks>
    void EvaluateScript(string source, string label);

    /// <summary>The concatenated descendant text of an element: a script element's program text.</summary>
    string TextContentOf(DomElement element);

    /// <summary>Fires a simple, non-bubbling event at an element — a script's <c>load</c>/<c>error</c>,
    /// covering inline <c>on*</c> attributes, assigned handler properties and listeners alike.</summary>
    void FireSimpleEvent(DomElement target, string type);
}
