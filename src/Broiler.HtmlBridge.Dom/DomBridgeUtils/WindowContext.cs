using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// A window handle, or <see cref="JsValue.Null"/> when the manager answered "no window".
    /// </summary>
    /// <remarks>
    /// <b>The CLR null these forms used to answer was the same answer spelled in a type only the
    /// middle of this path used.</b> The manager speaks JSEAL and so does <c>IMessagingHost</c>;
    /// these two delegators converted a handle to an engine object on the way out and the host
    /// converted it straight back, which named one window either way. What is kept is the
    /// distinction that conversion carried: a non-object answer becomes <c>null</c> for the page,
    /// not <see cref="JsValue.Missing"/>, because "no window" is a value a page reads.
    /// </remarks>
    internal static JsValue WindowOrNull(JsValue window) => window.IsObject ? window : JsValue.Null;
}
