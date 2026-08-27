using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The six <c>BarProp</c> objects on <c>Window</c> — <c>locationbar</c>, <c>menubar</c>,
/// <c>personalbar</c>, <c>scrollbars</c>, <c>statusbar</c> and <c>toolbar</c> (HTML §7.2.2). Pure
/// static, like <see cref="ScreenOrientationBinding"/>: it reads nothing and keeps no state.
/// </summary>
/// <remarks>
/// <para>
/// Every one reports <c>visible: false</c>, and that is a fact about this surface rather than a
/// default. A capture paints a document into a viewport and paints no browser user interface around
/// it: there is no location bar, menu bar, personal bar, status bar or toolbar, and no scrollbar is
/// painted either. The window geometry already published says the same thing independently —
/// <c>outerWidth</c> equals <c>innerWidth</c> and <c>outerHeight</c> equals <c>innerHeight</c>, so
/// nothing occupies space outside the viewport — and these values are chosen to agree with it rather
/// than to contradict it.
/// </para>
/// <para>
/// They were absent, which is a different and worse answer than <c>false</c>: the objects are
/// containers, so a page reading the documented <c>window.locationbar.visible</c> got
/// "Cannot get property visible of undefined" — an abort that costs the rest of the calling
/// function — rather than a boolean it could branch on. The commonest reader is chrome-detection
/// code deciding whether it is running in a popup or a full window, and that check runs early, in
/// the same setup pass as the rest of a page's environment sniffing.
/// </para>
/// <para>
/// <c>visible</c> is a getter with no setter. It is read-only in the specification, and there is
/// nothing here that could act on a write.
/// </para>
/// </remarks>
internal static class WindowBarPropBinding
{
    /// <summary>The names of the six BarProp members of <c>Window</c>.</summary>
    private static readonly string[] BarNames =
    [
        "locationbar",
        "menubar",
        "personalbar",
        "scrollbars",
        "statusbar",
        "toolbar",
    ];

    /// <summary>Installs all six <c>BarProp</c> objects on <paramref name="window"/>.</summary>
    public static void Install(JSObject window)
    {
        foreach (var name in BarNames)
            window.FastAddValue(name, Build(), JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>One <c>BarProp</c>. Each member gets its own object, as in a browser.</summary>
    private static JSObject Build()
    {
        var bar = new JSObject();

        bar.FastAddProperty("visible",
            new DomFunction((in _) => JSBoolean.False, "get visible"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        return bar;
    }
}
