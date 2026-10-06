using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// The changes to a document's tree that change an element's state as HTML defines it: a showing popover
/// taken out of its document or given another <c>popover</c> state, which closes it; a modal dialog taken
/// out of its document, which stops being modal (DomBridge/Inertness.cs); any change that can leave the
/// focused element unable to have focus, which the focus fixup checks at the next frame (DomBridge/Focus.cs);
/// and options added to or taken out of a select, or their <c>selected</c> attribute, or the select's
/// <c>multiple</c>, which change which of them are selected (DomBridge/SelectState.cs).
/// </summary>
public sealed partial class DomBridge
{
    private void OnElementStateMutation(DomMutationRecord record)
    {
        if (record.Type is DomMutationType.ChildList or DomMutationType.Attributes)
            QueueFocusFixup();

        switch (record.Type)
        {
            case DomMutationType.ChildList:
                foreach (var removed in record.RemovedNodes ?? [])
                {
                    HideRemovedPopovers(removed);
                    UnblockRemovedModalDialogs(removed);
                }

                OnSelectChildListMutation(record);
                break;

            case DomMutationType.Attributes when record.Target is DomElement element && record.AttributeName is { } name:
                if (name.Equals("popover", StringComparison.OrdinalIgnoreCase))
                    OnPopoverAttributeChanged(element, record.OldValue);
                else
                    OnSelectAttributeMutation(element, name, record.OldValue);
                break;
        }
    }
}
