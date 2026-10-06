using Broiler.Dom;
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
}
