using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The changes to a document's tree that change which of a select's options are selected (HTML's
/// selectedness setting algorithm), handed to the select binding: options added to or taken out of a
/// select, an option's <c>selected</c> attribute, a select's <c>multiple</c>.
/// </summary>
/// <remarks>
/// A select a script or the user has not changed reads its selectedness from its markup, which these
/// changes already are; from the first change on it holds its options' selectedness, and these keep it
/// right (Features/SelectBinding.cs).
/// </remarks>
public sealed partial class DomBridge
{
    private void OnSelectChildListMutation(DomMutationRecord record)
    {
        if (record.Target is not DomElement parent)
            return;

        // The select the options went into or came out of: the parent itself, or the select around an optgroup.
        var select = parent.TagName.Equals("select", StringComparison.OrdinalIgnoreCase)
            ? parent
            : Dom.Features.SelectBinding.SelectOf(parent);
        if (select is null)
            return;

        var inserted = new List<DomElement>();
        foreach (var added in record.AddedNodes ?? [])
        {
            if (added is not DomElement element)
                continue;

            if (Dom.Features.SelectBinding.IsHtmlOption(element))
                inserted.Add(element);
            else
                inserted.AddRange(element.Descendants().OfType<DomElement>().Where(Dom.Features.SelectBinding.IsHtmlOption));
        }

        if (inserted.Count > 0)
            _select.OnOptionsInserted(select, inserted);

        if ((record.RemovedNodes ?? []).Any(static removed => removed is DomElement))
            _select.OnOptionsRemoved(select);
    }

    private void OnSelectAttributeMutation(DomElement element, string name, string? oldValue)
    {
        if (name.Equals("selected", StringComparison.OrdinalIgnoreCase) && Dom.Features.SelectBinding.IsHtmlOption(element))
        {
            var present = HasAttr(element, "selected");
            if (present != (oldValue is not null))
                _select.OnSelectedAttributeChanged(element, present);
            return;
        }

        if (name.Equals("multiple", StringComparison.OrdinalIgnoreCase) &&
            element.TagName.Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            var wasMultiple = oldValue is not null;
            if (wasMultiple != HasAttr(element, "multiple"))
                _select.OnMultipleChanged(element, wasMultiple);
        }
    }

    /// <summary>
    /// HTML's legacy factory function <c>new Option(text, value, defaultSelected, selected)</c>, on
    /// <paramref name="window"/>: an <c>option</c> element with a text node when the text is not empty, a <c>value</c> attribute when a value is given, a <c>selected</c> attribute when
    /// <c>defaultSelected</c> is true -- and selected only when <c>selected</c> is, whatever its attribute
    /// says (measured). Called without <c>new</c> it throws, as Chromium does.
    /// </summary>
    /// <remarks>
    /// It did not exist, so the page that empties a select with <c>options.length = 0</c> and fills it
    /// again with <c>add(new Option(text, value))</c> -- the common way to do it -- threw at the first one.
    /// </remarks>
    private void RegisterOptionConstructor(JsValue window)
    {
        var realm = Realm;
        var constructor = realm.NewConstructor("Option", (in call) =>
        {
            if (call.NewTarget.IsMissing)
                throw call.Realm.Error(JsErrorKind.TypeError,
                    "Failed to construct 'Option': Please use the 'new' operator, this DOM object constructor cannot be called as a function.");

            var option = NoteCreatedByScript(CreateBridgeElement("option"));
            var text = call.Length > 0 && !call[0].IsUndefined ? call.Realm.ToJsString(call[0]) : string.Empty;
            if (text.Length > 0)
                option.AppendChild(CreateBridgeTextNode(text));
            if (call.Length > 1 && !call[1].IsUndefined)
                SetAttr(option, "value", call.Realm.ToJsString(call[1]));
            if (call.Length > 2 && call.Realm.ToBoolean(call[2]))
                SetAttr(option, "selected", string.Empty);

            // Its selectedness, not dirtied: a later selected attribute still selects it (measured).
            ((Dom.Features.ISelectHost)this).SetOptionState(option, call.Length > 3 && call.Realm.ToBoolean(call[3]), dirty: false);
            return WrapNode(option);
        }, 0);

        var optionInterface = realm.GetProperty(realm.Global, "HTMLOptionElement");
        var prototype = optionInterface.IsObject ? realm.GetProperty(optionInterface, "prototype") : JsValue.Undefined;
        if (prototype.IsObject)
            realm.SetProperty(constructor, "prototype", prototype);

        DefineWindowGlobal(window, "Option", constructor);
    }
}
