using System;
using System.Collections.Generic;
using System.Linq;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge;

// Explicit IFormSubmitHost implementation for the FormSubmitBinding feature module (Phase 3): the bridge
// exposes read access to the live per-node listener store via an explicit interface member, so the
// submit action never reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IFormSubmitHost
{
    private const string FormSubmitLogContext = "DomBridge.submit";

    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IFormSubmitHost.GetEventListeners(DomNode node)
        => GetEventListeners(node);

    void Dom.Features.IFormSubmitHost.RequestFormSubmission(DomElement form)
    {
        var index = IndexOfForm(form);
        if (index < 0)
        {
            // A form the script built but never inserted. There is nothing for the host to find
            // when it walks the serialized document, so saying "submit form 3" would name a form
            // that is not there.
            RenderLogger.LogDebug(LogCategory.JavaScript, FormSubmitLogContext,
                "form.submit() on a form that is not in the document; nothing to submit");
            return;
        }

        var action = ResolveFormAction(form);
        RenderLogger.LogDebug(LogCategory.JavaScript, FormSubmitLogContext,
            $"form.submit() requested for form {index} to {action}; the host builds the data set and decides whether to follow it");

        RequestNavigation(new NavigationRequest(action, NavigationKind.FormSubmit) { FormIndex = index });
    }

    /// <summary>
    /// The form's position among the document's forms, in document order, or <c>-1</c> when it is
    /// not in the document. The same walk <see cref="Elements"/> makes, so the host counting forms
    /// in the serialized document counts them in this order.
    /// </summary>
    private int IndexOfForm(DomElement form)
    {
        var seen = 0;
        foreach (var element in _document.InclusiveDescendants().OfType<DomElement>())
        {
            if (!string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase))
                continue;

            if (ReferenceEquals(element, form))
                return seen;

            seen++;
        }

        return -1;
    }

    /// <summary>
    /// The form's <c>action</c> resolved against the document, falling back to the document's own
    /// URL — which is what an absent or empty <c>action</c> means (HTML §4.10.21.3).
    /// </summary>
    private string ResolveFormAction(DomElement form)
    {
        var action = form.GetAttribute("action");
        if (string.IsNullOrWhiteSpace(action))
            return _pageUrl;

        return Uri.TryCreate(_pageUrl, UriKind.Absolute, out var baseUri)
            && Uri.TryCreate(baseUri, action, out var resolved)
                ? resolved.ToString()
                : action;
    }
}
