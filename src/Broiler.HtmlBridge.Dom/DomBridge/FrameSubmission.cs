using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// A submission into a frame -- a form whose target names one, or a form in a frame's own document -- which
/// the bridge performs itself, since it loads frames itself; and a frame's form that targets the page, which
/// the host is handed as the page's navigation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither went anywhere.</b> A submission into a frame was logged and dropped, and the host finds a page's
/// form by its place in the page's document, which a frame's form has none of -- so a form in a frame did
/// nothing at all. In Chromium the frame loads what the submission answers, and the page's URL stays
/// (measured).
/// </para>
/// <para>
/// The entry list is the one the form's <c>formdata</c> listeners saw, with their changes, encoded as the host
/// encodes a page's: URL-encoded into the query for <c>get</c>, and for <c>post</c> as the <c>enctype</c>
/// says -- URL-encoded, <c>multipart/form-data</c> or <c>text/plain</c>. A file entry is its name, or in
/// multipart its (empty) part.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>Where a submission goes: the page the host shows (no <see cref="Frame"/>), a frame, or nowhere.</summary>
    private readonly record struct SubmissionTarget(bool Refused, DomElement? Frame);

    /// <summary>
    /// HTML's "rules for choosing a navigable" for a submission: the form's own document for no target or
    /// <c>_self</c>, its parent's for <c>_parent</c>, the page for <c>_top</c>, the frame a name names; and for
    /// <c>_blank</c> or a name nothing has, a new window -- this window for the user's own submission (the
    /// window follows such a link in place too) and nothing for a script's, which a pop-up blocker stops.
    /// </summary>
    private SubmissionTarget ResolveSubmissionTarget(DomElement form, DomElement? submitter)
    {
        var document = GetOwningDocument(form);
        var declared = submitter is not null && TryGetAttribute(submitter, "formtarget", out var formTarget) ? formTarget
            : TryGetAttribute(form, "target", out var target) ? target
            : BaseTargetOf(document);
        var name = declared.Trim();
        var ownFrame = GetFrameForContentDocument(document);
        switch (name.ToLowerInvariant())
        {
            case "" or "_self":
                return new(Refused: false, ownFrame);
            case "_top":
                return new(Refused: false, null);
            case "_parent":
                return new(Refused: false, ownFrame is null ? null : GetFrameForContentDocument(GetOwningDocument(ownFrame)));
        }

        if (!name.Equals("_blank", StringComparison.OrdinalIgnoreCase) && FrameNamed(name) is { } named)
            return new(Refused: false, named);

        if (HasTransientActivation(document))
            return new(Refused: false, null);

        RenderLogger.LogDebug(LogCategory.JavaScript, FormSubmitLogContext,
            $"A script's submission into another window (target=\"{name}\") opens nothing");
        return new(Refused: true, null);
    }

    /// <summary>The frame, in the page or any frame in it, whose browsing context <paramref name="name"/> names.</summary>
    private DomElement? FrameNamed(string name)
    {
        var documents = new Queue<DomDocument>();
        documents.Enqueue(_document);
        while (documents.TryDequeue(out var document))
        {
            foreach (var element in document.Descendants().OfType<DomElement>())
            {
                if (!IsFrameContainerElement(element))
                    continue;

                if (string.Equals(_browsingContexts.NameOf(element), name, StringComparison.Ordinal))
                    return element;

                if (GetContentDocument(element) is { } content)
                    documents.Enqueue(content);
            }
        }

        return null;
    }

    /// <summary>Submits <paramref name="form"/> into <paramref name="frame"/>, which loads what the submission answers.</summary>
    private void SubmitIntoFrame(DomElement frame, DomElement form, DomElement? submitter, (int X, int Y) imagePoint, IReadOnlyList<FormDataEdit> edits)
    {
        var entries = ApplyFormDataEdits(BuildFormEntryList(form, submitter, imagePoint), edits);
        var action = ResolveFormActionOf(form, submitter);
        RenderLogger.LogDebug(LogCategory.JavaScript, FormSubmitLogContext, $"A submission into a frame, to {action}");
        if (SubmissionMethodOf(form, submitter) == "post")
        {
            RequestFrameNavigation(frame, new NavigationRequest(action, NavigationKind.FormSubmit), EncodeFormBody(entries, FormEncodingOf(form, submitter)));
            return;
        }

        RequestFrameNavigation(frame, new NavigationRequest(WithQuery(action, UrlEncodeEntries(entries)), NavigationKind.FormSubmit));
    }

    /// <summary>
    /// Submits a frame's <paramref name="form"/> into the page: a <c>get</c> is the page's navigation to the
    /// URL with the entries in it. A <c>post</c> is not performed: the host submits the page's own forms,
    /// which this one is not one of.
    /// </summary>
    private bool SubmitFromFrameIntoPage(DomElement form, DomElement? submitter, (int X, int Y) imagePoint, IReadOnlyList<FormDataEdit> edits)
    {
        if (SubmissionMethodOf(form, submitter) == "post")
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, FormSubmitLogContext,
                "A frame's form posting into the page is not performed: the host submits the page's own forms");
            return false;
        }

        var entries = ApplyFormDataEdits(BuildFormEntryList(form, submitter, imagePoint), edits);
        RequestNavigation(new NavigationRequest(WithQuery(ResolveFormActionOf(form, submitter), UrlEncodeEntries(entries)), NavigationKind.FormSubmit)
        {
            Initiator = DocumentContextFor(form),
        });
        return true;
    }

    /// <summary>The submission's action URL, resolved against the form's own document -- a frame's, or the page's.</summary>
    private string ResolveFormActionOf(DomElement form, DomElement? submitter)
    {
        if (GetFrameForContentDocument(GetOwningDocument(form)) is not { } frame)
            return ResolveFormAction(form, submitter);

        var action = submitter?.GetAttribute("formaction") ?? form.GetAttribute("action");
        var baseUrl = GetSubDocumentBaseUrl(frame);
        if (string.IsNullOrWhiteSpace(action))
            return _browsingContexts.TryGetLocation(frame, out var location) ? location : baseUrl;

        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, action, out var resolved)
            ? resolved.ToString()
            : action;
    }

    /// <summary>The changes a form's <c>formdata</c> listeners made, applied to its entry list as <c>FormData</c> made them.</summary>
    private static List<FormEntry> ApplyFormDataEdits(List<FormEntry> entries, IReadOnlyList<FormDataEdit> edits)
    {
        foreach (var edit in edits)
        {
            switch (edit.Kind)
            {
                case FormDataEditKind.Append:
                    entries.Add(new(edit.Name, edit.Value));
                    break;

                case FormDataEditKind.Delete:
                    entries.RemoveAll(entry => entry.Name == edit.Name);
                    break;

                case FormDataEditKind.Set:
                    var first = entries.FindIndex(entry => entry.Name == edit.Name);
                    if (first < 0)
                    {
                        entries.Add(new(edit.Name, edit.Value));
                        break;
                    }

                    entries[first] = new(edit.Name, edit.Value);
                    for (var i = entries.Count - 1; i > first; i--)
                    {
                        if (entries[i].Name == edit.Name)
                            entries.RemoveAt(i);
                    }

                    break;
            }
        }

        return entries;
    }

    /// <summary>The submission's encoding: the submitter's <c>formenctype</c>, else the form's <c>enctype</c>; URL-encoded unless either names one of the other two.</summary>
    private static string FormEncodingOf(DomElement form, DomElement? submitter)
    {
        var declared = (submitter?.GetAttribute("formenctype") ?? form.GetAttribute("enctype"))?.Trim();
        return declared?.ToLowerInvariant() is "multipart/form-data" or "text/plain" ? declared.ToLowerInvariant() : "application/x-www-form-urlencoded";
    }

    private static string UrlEncodeEntries(IReadOnlyList<FormEntry> entries) =>
        string.Join("&", entries.Select(static entry => FetchBinding.EncodeFormComponent(entry.Name) + "=" + FetchBinding.EncodeFormComponent(entry.Value)));

    /// <summary><paramref name="action"/> with its query replaced by <paramref name="query"/> (HTML "mutate action URL"); its fragment stays.</summary>
    private static string WithQuery(string action, string query)
    {
        var hash = action.IndexOf('#');
        var fragment = hash >= 0 ? action[hash..] : string.Empty;
        var withoutFragment = hash >= 0 ? action[..hash] : action;
        var question = withoutFragment.IndexOf('?');
        return (question >= 0 ? withoutFragment[..question] : withoutFragment) + "?" + query + fragment;
    }

    /// <summary>A <c>post</c> submission's body in <paramref name="encoding"/>, as the host encodes a page's.</summary>
    private static FrameRequestBody EncodeFormBody(IReadOnlyList<FormEntry> entries, string encoding)
    {
        switch (encoding)
        {
            case "text/plain":
                var text = new StringBuilder();
                foreach (var entry in entries)
                    text.Append(entry.Name).Append('=').Append(entry.Value).Append("\r\n");
                return new FrameRequestBody(Encoding.UTF8.GetBytes(text.ToString()), "text/plain");

            case "multipart/form-data":
                var boundary = "----BroilerFormBoundary" + Guid.NewGuid().ToString("N");
                var body = new StringBuilder();
                foreach (var entry in entries)
                {
                    body.Append("--").Append(boundary).Append("\r\n");
                    body.Append("Content-Disposition: form-data; name=\"").Append(entry.Name.Replace("\"", "%22", StringComparison.Ordinal)).Append('"');
                    if (entry.IsFile)
                        body.Append("; filename=\"").Append(entry.Value.Replace("\"", "%22", StringComparison.Ordinal)).Append("\"\r\nContent-Type: application/octet-stream");
                    body.Append("\r\n\r\n");
                    if (!entry.IsFile)
                        body.Append(entry.Value);
                    body.Append("\r\n");
                }

                body.Append("--").Append(boundary).Append("--\r\n");
                return new FrameRequestBody(Encoding.UTF8.GetBytes(body.ToString()), "multipart/form-data; boundary=" + boundary);

            default:
                return new FrameRequestBody(Encoding.UTF8.GetBytes(UrlEncodeEntries(entries)), "application/x-www-form-urlencoded");
        }
    }

    /// <summary>The request a frame's navigation with a body sends: a <c>post</c>.</summary>
    private static HttpRequestMessage PostRequest(string url, FrameRequestBody body)
    {
        var content = new ByteArrayContent(body.Content);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(body.ContentType);
        return new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
    }
}
