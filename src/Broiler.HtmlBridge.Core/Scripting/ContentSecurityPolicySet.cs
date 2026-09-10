namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// The Content-Security-Policies that govern one document. A script is admitted only when EVERY
/// policy in the set admits it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A document can be bound by more than one policy, and this repository used to assume it was
/// bound by at most one.</b> CSP is specified as a LIST — a document enforces every policy it was
/// delivered, and each one can only narrow what the others allow. Two of those arrive here.
/// A document may declare a policy in a <c>&lt;meta http-equiv&gt;</c> and receive another in a
/// <c>Content-Security-Policy</c> response header. And a document with a LOCAL SCHEME —
/// <c>about:srcdoc</c>, <c>about:blank</c>, <c>data:</c>, <c>blob:</c> — has no response of its own,
/// so it INHERITS its embedder's policy, on top of any it declares itself.
/// </para>
/// <para>
/// <b>Inherits, not replaces, and that is the whole point of the set.</b> With one slot, a frame
/// that declared a permissive policy of its own would overwrite the embedder's and a
/// <c>&lt;meta&gt;</c> inside a <c>srcdoc</c> would be a way out of the page's policy rather than a
/// document inside it. <c>SubDocumentContentSecurityPolicyTests</c> pins that in both directions.
/// </para>
/// <para>
/// <b>An empty set admits everything</b>, which is what "no policy was stated" has to mean, and it
/// is the default. That is deliberately the same answer the single nullable policy gave, so the
/// paths that never had a policy to consult behave exactly as they did.
/// </para>
/// <para>
/// Two slots rather than a list: the two ways a policy reaches a document cannot both be doubled —
/// a network document takes header plus meta and inherits nothing, and a local-scheme document
/// inherits the embedder's plus its own meta and has no response to carry a header. A third would
/// mean a rule this type does not know about, so it is better as a compile error than as a silently
/// dropped policy.
/// </para>
/// </remarks>
public readonly struct ContentSecurityPolicySet
{
    private readonly ContentSecurityPolicy? _first;
    private readonly ContentSecurityPolicy? _second;

    /// <summary>A set of the policies given; a <see langword="null"/> is simply not a policy.</summary>
    public ContentSecurityPolicySet(ContentSecurityPolicy? first, ContentSecurityPolicy? second = null)
    {
        // Normalised so that a set holding one policy always holds it in the first slot, which keeps
        // `IsEmpty` a test of the first slot alone and makes two sets of the same policies equal in
        // shape however they were built.
        if (first is null)
        {
            _first = second;
            _second = null;
        }
        else
        {
            _first = first;
            _second = ReferenceEquals(first, second) ? null : second;
        }
    }

    /// <summary>No policy governs the document, so nothing is refused.</summary>
    public static ContentSecurityPolicySet None => default;

    /// <summary>Whether any policy at all governs the document.</summary>
    public bool IsEmpty => _first is null;

    /// <inheritdoc cref="ContentSecurityPolicy.AllowsInlineScript"/>
    public bool AllowsInlineScript(string? nonce = null, string? scriptText = null) =>
        (_first is null || _first.AllowsInlineScript(nonce, scriptText)) &&
        (_second is null || _second.AllowsInlineScript(nonce, scriptText));

    /// <inheritdoc cref="ContentSecurityPolicy.AllowsExternalScript"/>
    public bool AllowsExternalScript(string scriptUrl, string? pageUrl, string? nonce = null) =>
        (_first is null || _first.AllowsExternalScript(scriptUrl, pageUrl, nonce)) &&
        (_second is null || _second.AllowsExternalScript(scriptUrl, pageUrl, nonce));

    /// <summary>
    /// Whether every policy in the set permits <c>eval</c>. An empty set permits it, as
    /// <see cref="ContentSecurityPolicy.AllowsEval"/> does when no policy was stated.
    /// </summary>
    public bool AllowsEval =>
        (_first is null || _first.AllowsEval) &&
        (_second is null || _second.AllowsEval);
}
