using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static double? ScrollCoordinateOption(IJsRealm realm, JsValue options, string propertyName)
    {
        var value = realm.GetProperty(options, propertyName);
        return value.IsNullish ? null : realm.ToNumber(value);
    }

    internal static string? ScrollBehaviorOption(IJsRealm realm, JsValue options)
    {
        var value = realm.GetProperty(options, "behavior");
        if (value.IsNullish)
            return null;

        var behavior = realm.ToJsString(value);
        return string.IsNullOrWhiteSpace(behavior) ? null : behavior;
    }
}
