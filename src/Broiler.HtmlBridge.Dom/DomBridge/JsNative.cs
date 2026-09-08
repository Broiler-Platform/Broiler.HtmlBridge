using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// The two constant-answer native functions the bridge still mints as the engine's own.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are adapters, and what pins them is named.</b> Two unmigrated feature modules build a
/// member out of one of these factories rather than out of <c>IJsValues.NewMethod</c>:
/// <c>Features/NavigatorCapabilityBinding.cs</c> (<c>plugins</c>/<c>mimeTypes</c>
/// <c>item</c>/<c>namedItem</c>/<c>refresh</c>) and <c>Features/NotificationBinding.cs</c>
/// (<c>notification.close</c>). Both are other groups' files, so the factories stay until they mint
/// their no-ops through a realm.
/// </para>
/// <para>
/// They are <see cref="JSFunction"/> rather than the non-constructable <c>DomFunction</c>, and that
/// difference is observable — <c>navigator.plugins.item.prototype</c> is an object and
/// <c>new navigator.plugins.item()</c> does not throw. It is preserved rather than corrected here:
/// straightening it is a behaviour change and belongs with whoever migrates those call sites.
/// </para>
/// <para>
/// The <c>TrueFunction</c> and <c>ZeroFunction</c> factories that stood beside these two were
/// deleted: their last call sites went when SvgElementBinding and the SMIL no-ops moved to the
/// realm, and nothing in the repository named either.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    private static readonly JSFunctionDelegate ReturnUndefinedDelegate = ReturnUndefined;
    private static readonly JSFunctionDelegate ReturnNullDelegate = ReturnNull;

    internal static JSFunction UndefinedFunction(string name, int length = 0) => new(ReturnUndefinedDelegate, name, length);

    internal static JSFunction NullFunction(string name, int length = 0) => new(ReturnNullDelegate, name, length);

    private static JSValue ReturnUndefined(in Arguments _) => JSUndefined.Value;

    private static JSValue ReturnNull(in Arguments _) => JSNull.Value;
}
