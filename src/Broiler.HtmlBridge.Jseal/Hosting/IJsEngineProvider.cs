namespace Broiler.HtmlBridge.Jseal;

/// <summary>
/// One JavaScript engine, as something the browser can choose.
/// </summary>
/// <remarks>
/// A provider is the whole of what "adding an engine" means: an implementation of this interface, an
/// <see cref="IJsRealm"/> over the engine's own realm, and a registration. Nothing in the bridge, the
/// rendering pipeline or the browser shell changes.
/// </remarks>
public interface IJsEngineProvider
{
    /// <summary>
    /// A stable identifier, lower-case and hyphenated — <c>broiler-js</c>, <c>broiler-vm</c>. This is
    /// what a configuration or an environment variable names, so it does not change with a release.
    /// </summary>
    string Name { get; }

    /// <summary>A one-line description for a diagnostic or an about page.</summary>
    string Description { get; }

    /// <summary>
    /// What realms from this provider will be able to do, before one is built.
    /// </summary>
    /// <remarks>
    /// A realm's own <see cref="IJsRealm.Capabilities"/> may be <em>narrower</em> — a page whose
    /// Content-Security-Policy forbids evaluation gets a realm without
    /// <see cref="JsCapabilities.GuestEval"/> from an engine that has it — but never wider. That is
    /// what lets a host decide whether an engine can serve a page at all without paying to build a
    /// realm and find out.
    /// </remarks>
    JsCapabilities Capabilities { get; }

    /// <summary>Builds a realm.</summary>
    IJsRealm CreateRealm(JsRealmOptions options);
}

/// <summary>
/// What a host asks of a realm at the moment it is built — the things that cannot be changed
/// afterwards because they change the shape of the runtime rather than a flag inside it.
/// </summary>
/// <remarks>
/// <see cref="AllowGuestEval"/> is the clearest case. A page whose policy forbids evaluation and one
/// that permits it get two differently shaped runtimes, and the refusal is a contract outcome the page
/// may catch rather than an engine consulting a policy object mid-execution. Broiler.VM's embedding
/// contract states this directly — a policy forbidding dynamic evaluation is expressed by registering
/// no artifact-provider capability — and it is the right shape on any engine.
/// </remarks>
public sealed class JsRealmOptions
{
    /// <summary>
    /// Whether the page may evaluate source of its own (<c>eval</c>, <c>new Function</c>). Default
    /// <see langword="true"/>; a host that has read a restrictive Content-Security-Policy passes
    /// <see langword="false"/>, and the realm is built without the capability.
    /// </summary>
    public bool AllowGuestEval { get; init; } = true;

    /// <summary>
    /// Whether every script is run in strict mode regardless of what it says.
    /// </summary>
    public bool ForceStrictMode { get; init; }

    /// <summary>
    /// The document's URL, when there is one. A provider that resolves module specifiers or caches
    /// compiled programs needs it as part of the identity of what it compiled.
    /// </summary>
    public string? DocumentUrl { get; init; }

    /// <summary>The default: an unrestricted realm with no document.</summary>
    public static JsRealmOptions Default { get; } = new();
}
