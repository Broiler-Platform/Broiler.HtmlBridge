using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>form.submit()</c> action, registered on every element wrapper, co-located as an HtmlBridge
/// feature module. On a <c>&lt;form&gt;</c> it submits the form as HTML's "submit" does when run "from
/// the <c>submit()</c> method": no <c>submit</c> event and no validation -- a page calls <c>submit()</c>
/// precisely to submit past both -- but the entry list and its <c>formdata</c> event.
/// <para>
/// <b>It fired a <c>submit</c> event of its own</b>, so a page's <c>submit</c> listener ran for it
/// and the one that cancels -- the common "check, then <c>preventDefault()</c>" listener -- stopped a
/// submission no browser would have stopped. A page that cancels its form's <c>submit</c> and later
/// calls <c>form.submit()</c> to send it after all was held back for good. A submission that does
/// fire <c>submit</c> is <c>requestSubmit()</c> or a submit button's click (<c>DomBridge/FormSubmission.cs</c>).
/// </para>
/// <para>
/// The bridge names the form and resolves its <c>action</c>, and the host builds the data set —
/// serializing a form is something it already does for keyboard and mouse submissions, and doing it
/// a second time here is how the two would drift.
/// </para>
/// </summary>
internal static class FormSubmitBinding
{
    /// <summary>
    /// <c>submit()</c> on an element wrapper. A no-op on anything that is not a <c>&lt;form&gt;</c>,
    /// which is what it has always been.
    /// </summary>
    /// <param name="host">The submission handover.</param>
    /// <param name="element">The element the member was installed for.</param>
    public static JsValue Submit(IFormSubmitHost host, DomElement element)
    {
        if (string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase))
            host.SubmitFromSubmitMethod(element);

        return JsValue.Undefined;
    }
}
