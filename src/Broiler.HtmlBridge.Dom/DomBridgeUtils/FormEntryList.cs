using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>The four control tags, plus whatever the custom-element registry adds.</summary>
    internal static readonly HashSet<string> ControlTags =
        new(StringComparer.Ordinal) { "input", "select", "textarea", "button" };

    /// <summary>
    /// Whether the control is disabled — by its own <c>disabled</c> attribute or by an ancestor
    /// <c>&lt;fieldset disabled&gt;</c>, which disables everything in it (HTML §4.10.15).
    /// </summary>
    internal static bool IsFormControlDisabled(DomElement control)
    {
        if (HasAttr(control, "disabled"))
            return true;

        for (var ancestor = ParentEl(control); ancestor is not null; ancestor = ParentEl(ancestor))
        {
            if (string.Equals(ancestor.TagName, "fieldset", StringComparison.OrdinalIgnoreCase) &&
                HasAttr(ancestor, "disabled"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a <c>FormData</c>'s entries, or answers <see langword="false"/> for anything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Recognised by shape rather than by identity, because this engine's <c>FormData</c> objects are
    /// plain objects carrying the interface's members rather than instances of a registered
    /// interface. Reading through <c>forEach</c> rather than the private entry list keeps this
    /// working for any object that really is one.
    /// </para>
    /// <para>
    /// <b>The realm is a parameter because every operation here needs one</b> — reading the three
    /// members, minting the collector, calling <c>forEach</c>, and the two string coercions. Those
    /// coercions are the observable ECMAScript ones, which is why they are
    /// <see cref="IJsValues.ToJsString"/> and not <c>JsValue.ToString</c>: an entry whose name or
    /// value is an object with its own <c>toString</c> participates, exactly as it did when the
    /// engine's <c>JSValue.ToString()</c> ran it.
    /// </para>
    /// </remarks>
    internal static bool TryReadFormDataEntries(
        IJsRealm realm, JsValue candidate, out List<KeyValuePair<string, string>> entries)
    {
        entries = [];
        var forEach = realm.GetProperty(candidate, "forEach");
        if (!forEach.IsFunction ||
            !realm.GetProperty(candidate, "append").IsFunction ||
            !realm.GetProperty(candidate, "getAll").IsFunction)
            return false;

        var collected = entries;
        var collector = realm.NewMethod("collect", (in call) =>
        {
            // forEach hands (value, name, formData), the order the Web IDL iterable declares.
            if (call.Length >= 2)
                collected.Add(new KeyValuePair<string, string>(
                    call.Realm.ToJsString(call[1]), call.Realm.ToJsString(call[0])));
            return JsValue.Undefined;
        }, 3);

        realm.Invoke(forEach, candidate, [collector]);
        return true;
    }
}
