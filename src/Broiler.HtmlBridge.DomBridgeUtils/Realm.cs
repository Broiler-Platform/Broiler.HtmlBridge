using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// The realm options a policy maps to: guest evaluation is allowed exactly when
    /// <see cref="ContentSecurityPolicy.AllowsEval"/> says so, and allowed when there is no policy.
    /// </summary>
    /// <remarks>
    /// One mapping for every path that adopts a context the host built: <c>RegisterDocumentCore</c>, with
    /// the bridge's policy, and <c>ScriptEngine</c>'s two document-free entry points, with the host's.
    /// </remarks>
    internal static JsRealmOptions RealmOptionsFor(ContentSecurityPolicy? policy) =>
        new() { AllowGuestEval = policy?.AllowsEval ?? true };
}
