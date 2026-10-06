using System.Collections.Generic;
using System.Linq;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// The Content-Security-Policies that govern one document. A script is admitted only when EVERY
/// policy in the set admits it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A document can be bound by more than one policy.</b> CSP is specified as a LIST — a document
/// enforces every policy it was delivered, and each one can only narrow what the others allow.
/// Two of those arrive here.
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
/// is the default.
/// </para>
/// <para>
/// A third way is a <c>javascript:</c> URL's document, which has no response either: HTML gives it a
/// clone of the replaced document's policy container, so it is bound by every policy that document
/// was -- delivered and declared alike -- and by any its own markup declares
/// (<see cref="Inheriting"/>). Chromium, measured: a policy from the replaced document's
/// <c>&lt;meta&gt;</c> refuses an image and <c>eval</c> in the new one. Two slots could not hold that,
/// so the set is a list; the constructor still takes the two ways that come in pairs.
/// </para>
/// </remarks>
public readonly struct ContentSecurityPolicySet
{
    // Never empty when set: no policy at all is null, so the default value is the empty set.
    private readonly ContentSecurityPolicy[]? _policies;

    /// <summary>A set of the policies given; a <see langword="null"/> is simply not a policy.</summary>
    public ContentSecurityPolicySet(ContentSecurityPolicy? first, ContentSecurityPolicy? second = null)
        : this(Normalise([first, second]))
    {
    }

    private ContentSecurityPolicySet(ContentSecurityPolicy[]? policies) => _policies = policies;

    /// <summary>
    /// A <c>javascript:</c> URL's document's: every policy of the document it replaced, and the one its
    /// own markup declares.
    /// </summary>
    public static ContentSecurityPolicySet Inheriting(ContentSecurityPolicySet replaced, ContentSecurityPolicy? declared) =>
        new(Normalise([.. replaced.Policies, declared]));

    /// <summary>No policy governs the document, so nothing is refused.</summary>
    public static ContentSecurityPolicySet None => default;

    /// <summary>Whether any policy at all governs the document.</summary>
    public bool IsEmpty => _policies is null;

    /// <summary>The policies, each of which has to admit what the document does.</summary>
    public IReadOnlyList<ContentSecurityPolicy> Policies => _policies ?? [];

    /// <inheritdoc cref="ContentSecurityPolicy.AllowsInlineScript"/>
    public bool AllowsInlineScript(string? nonce = null, string? scriptText = null) =>
        Policies.All(policy => policy.AllowsInlineScript(nonce, scriptText));

    /// <inheritdoc cref="ContentSecurityPolicy.AllowsExternalScript"/>
    public bool AllowsExternalScript(string scriptUrl, string? pageUrl, string? nonce = null) =>
        Policies.All(policy => policy.AllowsExternalScript(scriptUrl, pageUrl, nonce));

    /// <inheritdoc cref="ContentSecurityPolicy.AllowsWorker"/>
    public bool AllowsWorker(string workerUrl, string? pageUrl) =>
        Policies.All(policy => policy.AllowsWorker(workerUrl, pageUrl));

    /// <summary>
    /// Whether every policy in the set permits <c>eval</c>. An empty set permits it, as
    /// <see cref="ContentSecurityPolicy.AllowsEval"/> does when no policy was stated.
    /// </summary>
    public bool AllowsEval => Policies.All(static policy => policy.AllowsEval);

    // Without the nulls, and without a policy twice, so a policy that is both inherited and declared is
    // asked once; null when nothing is left.
    private static ContentSecurityPolicy[]? Normalise(IEnumerable<ContentSecurityPolicy?> policies)
    {
        var list = new List<ContentSecurityPolicy>();
        foreach (var policy in policies)
        {
            if (policy is not null && !list.Exists(existing => ReferenceEquals(existing, policy)))
                list.Add(policy);
        }

        return list.Count == 0 ? null : [.. list];
    }
}
