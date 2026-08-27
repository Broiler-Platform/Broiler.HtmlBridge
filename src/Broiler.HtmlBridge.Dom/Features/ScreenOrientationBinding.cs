using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>screen.orientation</c> — the <c>ScreenOrientation</c> object (Screen Orientation API §4).
/// Pure static, like <c>CryptoBinding</c>: it reads the screen dimensions it is handed and keeps no
/// state of its own.
/// </summary>
/// <remarks>
/// <para>
/// The type is derived from the screen's own shape rather than reported as a constant, because
/// that is what it means: a viewport wider than it is tall is landscape. It is always a
/// <c>-primary</c> type and the angle is always 0 — Broiler renders into a viewport that is never
/// rotated relative to its output, so there is no secondary orientation for it to be in and no
/// rotation to report. A page asking "which way up am I" gets an answer that is true of the surface
/// it is drawing on.
/// </para>
/// <para>
/// Absent, <c>screen.orientation.type</c> threw "Cannot get property type of undefined" rather than
/// reading as an unsupported API — the abort that costs the rest of the calling function.
/// Responsive layouts branch on this in the same setup pass that installs their resize handling.
/// </para>
/// <para>
/// <c>lock()</c> is not here. It exists to <em>change</em> the orientation, which a fixed viewport
/// cannot do, and its promise is required to reject when the orientation cannot be locked — a
/// rejection a page must handle. Supplying one that always rejects would add nothing a missing
/// method does not already tell a caller, and supplying one that resolves would be a lie.
/// </para>
/// </remarks>
internal static class ScreenOrientationBinding
{
    /// <summary>
    /// Builds the <c>ScreenOrientation</c> for a screen of the given size. The members are
    /// accessors so that a screen whose size is re-evaluated reports the orientation that follows
    /// from it.
    /// </summary>
    /// <param name="width">Screen width in CSS pixels.</param>
    /// <param name="height">Screen height in CSS pixels.</param>
    public static JSObject Build(int width, int height)
    {
        var orientation = new JSObject();

        orientation.FastAddProperty("type",
            new DomFunction((in _) => new JSString(TypeOf(width, height)), "get type"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // The angle between the current orientation and the device's natural one. Broiler's output
        // surface is its natural orientation, so the two never differ.
        orientation.FastAddProperty("angle",
            new DomFunction((in _) => new JSNumber(0), "get angle"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // onchange is a settable event-handler attribute that nothing ever fires here, because the
        // orientation cannot change. It is present because a page assigns to it unconditionally,
        // and null is the value the attribute has before anything is assigned.
        JSValue onChange = JSNull.Value;
        orientation.FastAddProperty("onchange",
            new DomFunction((in _) => onChange, "get onchange"),
            new DomFunction((in a) => onChange = a.Length > 0 ? a[0] : JSNull.Value, "set onchange"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // unlock() releases a lock; with no way to take one there is never a lock to release, which
        // makes doing nothing the specified behaviour rather than a stub.
        orientation.FastAddValue("unlock",
            DomBridge.UndefinedFunction("unlock", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return orientation;
    }

    /// <summary>
    /// A square screen is reported as portrait, which is how the specification resolves the tie:
    /// the natural orientation is portrait unless the width exceeds the height.
    /// </summary>
    private static string TypeOf(int width, int height) =>
        width > height ? "landscape-primary" : "portrait-primary";
}
