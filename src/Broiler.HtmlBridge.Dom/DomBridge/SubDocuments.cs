using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.JavaScript.Modules;
using Broiler.Net.Http;
using Broiler.Dom;
using Broiler.Dom.Html;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // The nested-browsing-context state — the per-container sub-document/sub-window JS-object identity,
    // location/base-URL caches, object-load-failure and onload-fired marks, the reverse
    // sub-window→container map, the current-window override, and the severed content-document
    // maps — is owned by _browsingContexts (BrowsingContextManager, declared in DomBridge.cs).
    // The builders / resolvers / onload dispatch below stay bridge-owned and reach it through that owner.

    private DomDocument? GetContentDocument(DomElement containerElement) =>
        _browsingContexts.GetContentDocument(containerElement);

    /// <summary>The frame element a severed sub-document belongs to, or null for the main /
    /// a detached (createDocument) document. Replaces <c>ParentEl(#subdoc-root)</c>.</summary>
    private DomElement? GetFrameForContentDocument(DomNode? docRoot) =>
        docRoot is DomDocument document ? _browsingContexts.GetContainerForDocument(document) : null;

    private void LinkContentDocument(DomElement containerElement, DomDocument document) =>
        _browsingContexts.LinkContentDocument(containerElement, document);

    /// <summary>The content-document resolver handed to the layout view: maps a
    /// nested-browsing-context container to its severed sub-document so the box builder
    /// projects it as a sub-viewport and composes its geometry into the main frame.</summary>
    private DomDocument? ResolveContentDocumentForRender(DomElement containerElement) =>
        GetContentDocument(containerElement);

    private void InvalidateCachedSubDocument(DomElement containerElement)
    {
        // Order preserved from the earlier code: release the old content document's element
        // runtime state while the maps still reference it, then unlink and drop the per-container caches.
        if (_browsingContexts.GetContentDocument(containerElement) is { } existingDocument)
        {
            RemoveElementsRecursive(existingDocument);
            _browsingContexts.UnlinkContentDocument(containerElement);
            RemoveFrameDocumentContext(existingDocument);
        }

        _browsingContexts.RemoveContainerCaches(containerElement);
    }

    /// <summary>
    /// Fires the onload event handler on a nested-browsing-context container (iframe/object/frame)
    /// after its sub-document has been loaded. The handler is only fired once per element.
    /// Handles both property-based handlers (element.onload = function) and
    /// attribute-based handlers (setAttribute("onload", code)).
    /// </summary>
    private void FireSubDocumentOnload(DomElement element)
    {
        if (_realm is null) return;
        if (_browsingContexts.HasOnloadFired(element)) return;

        var tag = element.TagName?.ToLowerInvariant();
        if (!IsNestedBrowsingContextContainer(tag)) return;

        var hasSrcDoc = tag == "iframe" && HasAttr(element, "srcdoc");
        var resourceUrl = hasSrcDoc ? "about:srcdoc" : GetSubResourceUrl(element);
        if (string.IsNullOrWhiteSpace(resourceUrl) && !hasSrcDoc) return;

        // Ensure the sub-document is loaded (this triggers the fetch if needed)
        GetOrCreateSubDocument(element);

        _browsingContexts.MarkOnloadFired(element);

        // Fire the onload handler
        try
        {
            // The event object is minted through the realm, and the dispatcher takes that handle as
            // it is.
            var evt = Realm.NewObject();
            Realm.DefineValue(evt, "type", JsValue.String("load"));
            Realm.DefineValue(evt, "bubbles", JsValue.False);
            _eventDispatch.DispatchEventOnElement(element, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.FireSubDocumentOnload",
                $"onload handler error for <{tag}>: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Recursively fires onload for all iframe/object/frame descendants of an element.
    /// Called when a subtree containing nested browsing contexts is added to the document.
    /// </summary>
    private void FireDescendantOnloads(DomElement element)
    {
        // Snapshot before iterating: FireSubDocumentOnload runs a sub-document's
        // onload handler, whose script can structurally mutate element.Children
        // mid-walk (append/remove nodes). Enumerating the live collection then
        // throws "Collection was modified" (crash signature
        // DomBridge.FireDescendantOnloads). SnapshotChildren also tolerates a
        // concurrent structural race, like the other DomBridge tree walks.
        foreach (var child in SnapshotChildren(element))
        {
            var childTag = child.TagName?.ToLowerInvariant();
            if (IsNestedBrowsingContextContainer(childTag))
            {
                FireSubDocumentOnload(child);
            }
            FireDescendantOnloads(child);
        }
    }

    /// <summary>
    /// Returns <c>true</c> if the resource for the given <c>&lt;object&gt;</c> element
    /// failed to load (HTTP 404, file not found, etc.), meaning fallback content
    /// should be visible and contentDocument should return null.
    /// </summary>
    private bool IsObjectLoadFailed(DomElement objectElement)
    {
        if (_browsingContexts.HasObjectLoadFailed(objectElement))
            return true;

        // Check if this is the first access — probe the resource
        var resourceUrl = GetSubResourceUrl(objectElement);
        if (string.IsNullOrWhiteSpace(resourceUrl))
            return false; // No data attribute → empty sub-document, not a failure

        var (_, contentType, _, _) = TryFetchSubResource(
            resourceUrl, GetInheritedSubDocumentBaseUrl(objectElement), objectElement);
        if (string.Equals(contentType, FetchFailedContentType, StringComparison.Ordinal))
        {
            _browsingContexts.MarkObjectLoadFailed(objectElement);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets or creates a full sub-document object for iframe/object elements.
    /// The sub-document has its own DOM tree, createElement, getElementById, etc.
    /// For same-origin HTTP/HTTPS resources, attempts to fetch and parse the content.
    /// Non-HTML resources (by extension or Content-Type) get a minimal empty document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything inside is JSEAL: the document object is built through the realm by
    /// <see cref="Dom.Features.SubDocumentBinding.Build"/> and the per-container cache in
    /// <see cref="Dom.Runtime.BrowsingContextManager"/> holds the handle it answered with.
    /// </para>
    /// </remarks>
    internal JsValue GetOrCreateSubDocument(DomElement containerElement)
    {
        if (_browsingContexts.TryGetSubDocument(containerElement, out var cached))
            return cached;

        var executeHtmlScripts = false;
        string? htmlToExecute = null;

        // The policy this frame is bound by IN ADDITION to any it declares in its own markup.
        //
        // A document with a local scheme -- about:srcdoc, about:blank, data:, blob: -- has no
        // response of its own to carry a policy, so it INHERITS ITS EMBEDDER'S, which is this
        // bridge's own Csp. A document fetched over the network does not inherit; it is bound by
        // what its own response delivered, which TryFetchSubResource now answers alongside the
        // content. Either way this is a policy the document did not write, enforced ALONGSIDE any
        // it did -- never instead of one.
        ContentSecurityPolicy? deliveredPolicy = null;

        // The frame document's request context (Network.cs): from the container document's, the URL
        // the document actually came from, and the container's sandbox attribute. Recorded against the
        // new document before its scripts run, so they, and the frames it embeds, are attributed to it.
        DocumentRequestContext? frameContext = null;

        DomDocument? docRoot = GetContentDocument(containerElement);
        if (docRoot == null)
        {
            if (string.Equals(containerElement.TagName, "iframe", StringComparison.OrdinalIgnoreCase) &&
                TryGetAttribute(containerElement, "srcdoc", out var srcDoc))
            {
                deliveredPolicy = Csp;
                _browsingContexts.SetLocation(containerElement, "about:srcdoc");
                _browsingContexts.SetBaseUrl(containerElement, GetInheritedSubDocumentBaseUrl(containerElement));
                docRoot = BuildSubDocumentFromHtml(srcDoc, containerElement);
                // A srcdoc document is its creator's: its cookie URL and origin are the container
                // document's (an opaque origin when sandboxed).
                frameContext = CreateFrameDocumentContext(containerElement, documentUrl: null);
                htmlToExecute = srcDoc;
                executeHtmlScripts = true;
            }
            else
            {
                // Determine the resource URL for this container
                var resourceUrl = GetSubResourceUrl(containerElement);
                var resolvedUrl = ResolveSubResourceUrl(resourceUrl, GetInheritedSubDocumentBaseUrl(containerElement));
                if (!string.IsNullOrWhiteSpace(resolvedUrl))
                {
                    _browsingContexts.SetLocation(containerElement, resolvedUrl);
                    _browsingContexts.SetBaseUrl(containerElement, resolvedUrl);
                }

                var localScheme = IsLocalSchemeSubResource(resourceUrl);
                deliveredPolicy = localScheme ? Csp : null;

                var (fetchedContent, contentType, responsePolicy, documentUrl) =
                    TryFetchSubResource(resourceUrl, GetInheritedSubDocumentBaseUrl(containerElement), containerElement);

                // The frame is where its response came from, not where its src pointed: after a
                // redirect, location and the base its relative URLs resolve against are the final URL.
                if (documentUrl is not null &&
                    IsHttpUrl(documentUrl) &&
                    !string.Equals(documentUrl, resolvedUrl, StringComparison.Ordinal))
                {
                    _browsingContexts.SetLocation(containerElement, documentUrl);
                    _browsingContexts.SetBaseUrl(containerElement, documentUrl);
                }

                // An HTTP(S) frame that produced no response at all is an error document with an
                // opaque origin; about:blank and anything unloadable inherit from the container.
                frameContext = documentUrl is null && IsHttpUrl(resolvedUrl)
                    ? CreateFrameDocumentContext(containerElement, resolvedUrl, opaque: true)
                    : CreateFrameDocumentContext(containerElement, documentUrl);

                // A network document does not inherit, and is bound by what its response delivered.
                // A local-scheme one has no response, so a policy from one would be a contradiction.
                if (!localScheme)
                    deliveredPolicy = responsePolicy;

                if (!string.IsNullOrEmpty(fetchedContent) &&
                    IsXmlContentType(contentType))
                {
                    // XML/SVG/XHTML content → parse with XML parser
                    docRoot = BuildSubDocumentFromXml(fetchedContent, contentType, containerElement, deliveredPolicy);
                }
                else if (!string.IsNullOrEmpty(fetchedContent) &&
                    (contentType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                     string.IsNullOrEmpty(contentType)))
                {
                    // HTML content → parse with HTML parser
                    docRoot = BuildSubDocumentFromHtml(fetchedContent, containerElement);
                    htmlToExecute = fetchedContent;
                    executeHtmlScripts = true;
                }
                else if (!string.IsNullOrEmpty(fetchedContent) &&
                         contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                {
                    // text/plain (or other text/* types) → document with pre-formatted text
                    docRoot = BuildSubDocumentWithText(fetchedContent, containerElement);
                }
                else
                {
                    // Default: create an empty sub-document structure
                    // (binary resources like image/png, fetch failures, about:blank, etc.)
                    docRoot = BuildEmptySubDocument(containerElement);
                }
            }
        }

        // A document that was already linked (created before its frame was first reached) keeps the
        // context it was given then, or is its creator's initial about:blank document.
        if (frameContext is not null || !_frameDocumentContexts.ContainsKey(docRoot))
            SetFrameDocumentContext(docRoot, frameContext ?? CreateFrameDocumentContext(containerElement, documentUrl: null));

        var doc = _subDocuments.Build(docRoot);
        // The frame document's own document.cookie, for its own context: a cross-site frame reads
        // the cookies a third-party context may, and a sandboxed one gets a SecurityError. Looked up
        // at each access, so a document the frame has since replaced is cookie-averse.
        DefineDocumentCookie(doc, () => _frameDocumentContexts.TryGetValue(docRoot, out var context) ? context : null);
        _browsingContexts.SetSubDocument(containerElement, doc);
        if (executeHtmlScripts && !string.IsNullOrEmpty(htmlToExecute))
            ExecuteSubDocumentScripts(containerElement, htmlToExecute, deliveredPolicy);
        return doc;
    }

    private string GetInheritedSubDocumentBaseUrl(DomElement containerElement)
    {
        var parentFrame = GetFrameForContentDocument(GetOwningDocument(containerElement));
        if (parentFrame != null &&
            _browsingContexts.TryGetBaseUrl(parentFrame, out var parentBaseUrl) &&
            !string.IsNullOrWhiteSpace(parentBaseUrl))
        {
            return parentBaseUrl;
        }

        return _pageUrl;
    }

    private string GetSubDocumentBaseUrl(DomElement containerElement)
    {
        return _browsingContexts.TryGetBaseUrl(containerElement, out var baseUrl) &&
               !string.IsNullOrWhiteSpace(baseUrl)
            ? baseUrl
            : GetInheritedSubDocumentBaseUrl(containerElement);
    }

    private string ResolveSubResourceUrl(string resourceUrl, string? baseUrl = null)
    {
        if (string.IsNullOrWhiteSpace(resourceUrl))
            return string.Empty;

        // Frames adopt the one shared resolver — same absolute-stays / relative-resolves /
        // else-empty behaviour that script and CSP already share via UrlResolver.
        var effectiveBaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? _pageUrl : baseUrl;
        return UrlResolver.Resolve(resourceUrl, effectiveBaseUrl)?.AbsoluteUri ?? string.Empty;
    }

    /// <param name="deliveredPolicy">
    /// A Content-Security-Policy this frame is bound by that its own markup did not declare: the
    /// embedder's, when the frame has a local scheme and inherits it, or the one its response
    /// header carried. It is enforced ALONGSIDE any policy the frame's markup declares, so a
    /// permissive <c>&lt;meta&gt;</c> inside a <c>srcdoc</c> cannot widen what the page allowed.
    /// </param>
    private void ExecuteSubDocumentScripts(
        DomElement containerElement,
        string html,
        ContentSecurityPolicy? deliveredPolicy = null)
    {
        if (_realm is null || string.IsNullOrWhiteSpace(html))
            return;

        // The scripts below are CLASSIC SCRIPTS -- a sub-document's script elements -- so they go
        // through IJsSource.EvaluateClassicScript. Their script-src decision was already taken, by
        // ScriptExtractionService against the policy set it builds: the deliveredPolicy argument,
        // when there is one, and the first policy the frame's own markup declares.
        //
        // 'unsafe-eval' does not govern a script element, and a realm narrowed by a policy that
        // forbids evaluation must still run these -- which is what the classic member exists to
        // express. The MODULE ROOTS below stay on the context because they need a JSModuleContext the
        // source contract does not describe at all.
        // The frame's scripts are fetched as the frame document's, through the profile transport when
        // the bridge has one.
        var frameContext = FrameDocumentContext(containerElement);
        var extraction = ScriptExtractionService.ExtractAll(
            html, GetSubDocumentBaseUrl(containerElement), deliveredPolicy, ScriptFetchFor(frameContext));

        // The same policy set ExtractAll checked the frame's scripts against, kept for the modules the
        // frame asks for once they run: its module roots' imports and its classic scripts' import().
        if (GetContentDocument(containerElement) is { } frameDocument)
            SetFrameScriptPolicies(frameDocument, new ContentSecurityPolicySet(deliveredPolicy, ContentSecurityPolicy.FromHtml(html)));
        if (extraction.Scripts.Count == 0 &&
            extraction.AsyncScripts.Count == 0 &&
            extraction.DeferredScripts.Count == 0 &&
            extraction.ModuleRoots.Count == 0)
            return;

        var subWindow = _subWindows.GetOrCreate(containerElement);

        // What the frame's scripts declare has to end up on the frame's own window, so a parent page
        // can reach it as frames[0].window.foo. They are evaluated in the shared context, so the
        // globals they add are identified by diffing against this snapshot.
        // See DomBridge/SubDocuments.Loading.cs.
        var globalsBefore = GlobalOwnPropertyNames();

        // Non-null: the early return above established it, and the loops below run inside a lambda
        // that would otherwise re-test a field it cannot see change.
        var realm = Realm;

        RunWithWindowContext(subWindow, () =>
        {
            // The three buckets run in three passes rather than in document order, and each pass
            // numbers its own scripts from zero -- see RunSubDocumentScripts for what the label says.
            RunSubDocumentScripts(realm, extraction.Scripts, "subdocument:");
            RunSubDocumentScripts(realm, extraction.AsyncScripts, "subdocument:async:");
            RunSubDocumentScripts(
                realm, extraction.DeferredScripts, "subdocument:defer:", "Sub-document deferred script error");

            // ES modules run last (they are deferred), through the engine's own module
            // machinery — exactly like the main page (ScriptEngine.RunPageScripts). This needs a module
            // realm, so it runs only when the sub-document shares an engine-driven parent context (a
            // JSModuleContext / BridgeModuleContext) and the engine binds static imports. The string-rewriting
            // EsModuleLinker fallback was retired, so a sub-document module under a plain-JSContext parent
            // (no top-level modules on the host page) is not executed — a documented limitation of the
            // engine-only module path.
            if (_jsContext is JSModuleContext subModuleContext
                && EngineModuleSupport.Available
                && extraction.ModuleRoots.Count > 0)
            {
                QueueSubDocumentModuleRoots(containerElement, subWindow, subModuleContext, extraction.ModuleRoots);
            }

            // Recorded, not published: the window these scripts ran against is a re-entrant
            // throwaway that the outer GetOrCreate replaces. See DomBridge/SubDocuments.Loading.cs.
            RecordSubDocumentGlobals(containerElement, globalsBefore);
        });
    }

    /// <summary>
    /// Queues a frame's module roots to start, in document order, as a job of the frame's current
    /// document: after the script that loaded the frame has finished (modules are deferred), inside the
    /// frame's window context, and not at all if the frame has navigated away by then.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every part of a frame's module runs as a job of the frame's window.</b> The roots run on the
    /// top document's module context, and are started from the job
    /// (<see cref="BridgeModuleContext.StartDocumentRoot"/>) rather than evaluated on the engine's worker
    /// (<c>RunScriptAsync</c>): the load, the evaluation and every continuation after it are posted to
    /// the frame's job queue, which runs each in the frame's window context. Nothing waits for them, so
    /// a module whose top-level <c>await</c> waits for a timer or an event holds up no drain -- not the
    /// load, and not a host that steps the page from its UI thread -- and no part of it can outlast a
    /// wait and resume as the embedding page. Its static imports are read synchronously, as the frame's
    /// classic scripts are, each within the scripts' fetch budget.
    /// </para>
    /// <para>
    /// That needs the host's microtask queue and the engine's job seam
    /// (<see cref="MicroTaskQueue"/>, <see cref="EngineJobs"/>, both set by <c>ScriptEngine</c>) and
    /// the bridge's own module context. Without them there is nothing that would run the frame's module
    /// as the frame, so it is not run, and that is logged.
    /// </para>
    /// </remarks>
    private void QueueSubDocumentModuleRoots(
        DomElement containerElement,
        JsValue subWindow,
        JSModuleContext moduleContext,
        IReadOnlyList<Broiler.HtmlBridge.Scripting.ModuleRoot> roots)
    {
        if (moduleContext is not BridgeModuleContext bridgeModules ||
            MicroTaskQueue is null ||
            EngineJobs is null)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.ExecuteSubDocumentScripts",
                $"Sub-document module roots not run: {roots.Count} root(s) need the bridge's module context and a host job queue to run as their frame.");
            return;
        }

        // Recorded as the frame document's before anything is requested -- under keys and a base of
        // this document's own, which the page's roots cannot share, and under the frame's own policies
        // rather than the page's.
        var registered = bridgeModules.RegisterDocumentRoots(
            roots, FrameModuleClient(containerElement), GetSubDocumentBaseUrl(containerElement));

        _windowContext.TryEnqueueJob(subWindow, () =>
        {
            foreach (var root in registered)
            {
                try
                {
                    var run = bridgeModules.StartDocumentRoot(root);
                    if (run.IsCompleted)
                        run.GetAwaiter().GetResult();
                    else
                        LogSubDocumentModuleFailure(run, root.Key);
                }
                catch (Exception ex)
                {
                    RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.ExecuteSubDocumentScripts",
                        $"Sub-document module root {root.Key} error: {ex.Message}", ex);
                }
            }
        });
    }

    /// <summary>
    /// Evaluates one bucket of a sub-document's classic scripts, numbering the bucket from zero.
    /// </summary>
    /// <remarks>
    /// The label is the location a stack frame reports, so it names the bucket and the position
    /// within it: a frame's third deferred script is a thing a reader can find.
    /// </remarks>
    private static void RunSubDocumentScripts(
        IJsRealm realm,
        IEnumerable<string> scripts,
        string labelPrefix,
        string errorMessage = "Sub-document script error")
    {
        var ordinal = 0;

        foreach (var script in scripts)
        {
            try
            {
                realm.EvaluateClassicScript(script, $"{labelPrefix}{ordinal++}");
            }
            catch (Exception ex)
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.ExecuteSubDocumentScripts",
                    $"{errorMessage}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Creates a minimal empty sub-document structure (html > head + body).
    /// </summary>
    private DomDocument BuildEmptySubDocument(DomElement containerElement)
    {
        var document = CreateBrowsingContextDocument();

        var htmlEl = CreateBridgeElement("html");
        document.AppendChild(htmlEl);

        var headEl = CreateBridgeElement("head");
        htmlEl.AppendChild(headEl);

        var bodyEl = CreateBridgeElement("body");
        htmlEl.AppendChild(bodyEl);

        LinkContentDocument(containerElement, document);

        return document;
    }

    /// <summary>
    /// Creates a sub-document with plain text content wrapped in a <c>&lt;pre&gt;</c> element.
    /// Used for <c>text/plain</c> resources.
    /// </summary>
    private DomDocument BuildSubDocumentWithText(string textContent, DomElement containerElement)
    {
        var document = CreateBrowsingContextDocument();

        var htmlEl = CreateBridgeElement("html");
        document.AppendChild(htmlEl);

        var headEl = CreateBridgeElement("head");
        htmlEl.AppendChild(headEl);

        var bodyEl = CreateBridgeElement("body");
        htmlEl.AppendChild(bodyEl);

        // Wrap text content in <pre> element
        var preEl = CreateBridgeElement("pre");
        bodyEl.AppendChild(preEl);

        var textNode = CreateBridgeTextNode(textContent);
        preEl.AppendChild(textNode);

        LinkContentDocument(containerElement, document);

        return document;
    }

    /// <summary>
    /// Attempts to fetch a sub-resource URL and return its content along with the
    /// detected content type. Returns <c>(null, contentType)</c> for non-HTML resources,
    /// about:blank, empty URLs, or when the fetch fails.
    /// Supports <c>data:</c> URIs, <c>file://</c> URLs, and <c>http(s)://</c> URLs.
    /// </summary>
    /// <summary>
    /// Fetches a sub-resource, answering its text, its content type, and any Content-Security-Policy
    /// its response delivered.
    /// </summary>
    /// <remarks>
    /// <b>The third element used not to exist, and its absence was a hole rather than a
    /// simplification.</b> A document's policy reaches it in a response header as readily as in a
    /// <c>&lt;meta&gt;</c>, and this method held the whole response and returned two strings out of
    /// it — so a frame served with <c>Content-Security-Policy: script-src 'none'</c> ran its scripts,
    /// and the code that filtered them looked as though it had asked. Only the HTTP branch can
    /// deliver one: a file, a data URI and a local WPT mapping have no response to carry a header,
    /// and answer <see langword="null"/> because that is true of them and not because it was not
    /// looked for.
    /// <para>
    /// <b>The fourth element is the URL the document came from</b>: the final URL of an HTTP
    /// response (after redirects, whatever its status), the <c>data:</c> or <c>file:</c> URL read
    /// locally, or <see langword="null"/> when nothing was loaded (about:blank, a failed request, a
    /// local-base-path file with no URL of its own). The frame's location and request context are
    /// built from it.
    /// </para>
    /// <para>
    /// An HTTP(S) frame is requested as a nested navigation of <paramref name="container"/>'s
    /// document, initiated by that document, through the profile transport when the bridge has one:
    /// the transport sends and stores the profile's cookies for each redirect hop, with the frame's
    /// same-site status taken from its container's ancestry.
    /// </para>
    /// </remarks>
    private (string? content, string contentType, ContentSecurityPolicy? policy, string? documentUrl) TryFetchSubResource(
        string resourceUrl,
        string? baseUrl,
        DomElement container)
    {
        if (string.IsNullOrWhiteSpace(resourceUrl))
            return (null, string.Empty, null, null);

        // about:blank gets an empty document (default behavior)
        if (string.Equals(resourceUrl, "about:blank", StringComparison.OrdinalIgnoreCase))
            return (null, "text/html", null, null);

        // Handle data: URIs — decode and return content directly
        if (resourceUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var (mimeType, body) = DecodeDataUriParts(resourceUrl);
            if (string.Equals(mimeType, "text/html", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mimeType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(mimeType))
                return (!string.IsNullOrEmpty(body) ? body : null, mimeType, null, resourceUrl);
            // Non-HTML data URIs: return body with detected MIME type
            return (!string.IsNullOrEmpty(body) ? body : null, mimeType, null, resourceUrl);
        }

        // Detect content type from extension for non-HTML resources
        var extensionMime = GetMimeTypeForExtension(resourceUrl);

        // Try local base path first (before URL resolution and HTTP fetch)
        if (!string.IsNullOrEmpty(_resources.LocalBasePath))
        {
            var localResult = TryReadLocalResource(resourceUrl, extensionMime);
            if (localResult.content != null || localResult.contentType != string.Empty)
                return (localResult.content, localResult.contentType, null, null);
        }

        // Resolve relative URL against page URL. An absolute URL keeps its raw string so the scheme
        // checks below (file:// / http(s)) see the exact original prefix; only the relative case goes
        // through the shared resolver.
        string resolvedUrl;
        if (Uri.TryCreate(resourceUrl, UriKind.Absolute, out _))
        {
            resolvedUrl = resourceUrl;
        }
        else if (UrlResolver.Resolve(resourceUrl, string.IsNullOrWhiteSpace(baseUrl) ? _pageUrl : baseUrl) is { } resolved)
        {
            resolvedUrl = resolved.AbsoluteUri;
        }
        else
        {
            return (null, extensionMime, null, null);
        }

        // Handle file:// URLs — read directly from local filesystem, for a file: document only
        // (LocalFileAccess). An http(s) page's frame of a local file is a network error: its document
        // is empty and, being a file: URL's, opaque.
        if (resolvedUrl.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            if (!LocalFileAccess.AllowedFor(DocumentContextFor(container).DocumentUrl))
            {
                RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.TryFetchSubResource",
                    $"Refused local document '{resolvedUrl}' for a document that is not a file: document.");
                return (null, FetchFailedContentType, null, resolvedUrl);
            }

            var fileResult = TryReadFileResource(resolvedUrl, extensionMime);
            return (fileResult.content, fileResult.contentType, null, resolvedUrl);
        }

        // Only fetch HTTP/HTTPS URLs
        if (!IsHttpUrl(resolvedUrl))
            return (null, extensionMime, null, null);

        // Off by default; see ResourceTrace. Traced at this level because the sub-document's decoded
        // text and its resolved content type are both known here, and a non-success status is a
        // meaningful outcome the loader below reports as an ordinary response.
        var attempt = ResourceTrace.Begin(ResourceTraceKind.SubDocument, resolvedUrl);
        try
        {
            using var transportResponse = _resources
                .GetAsync(resolvedUrl, FrameNavigationRequest(container))
                .GetAwaiter()
                .GetResult();
            var response = transportResponse.Message;
            var finalUrl = transportResponse.FinalUrl.AbsoluteUri;
            if (!response.IsSuccessStatusCode)
            {
                attempt.Completed(null, (int)response.StatusCode);
                return (null, FetchFailedContentType, null, finalUrl);
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? extensionMime;
            var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            attempt.Completed(content, (int)response.StatusCode, contentType);
            return (content, contentType, PolicyFromResponse(response), finalUrl);
        }
        catch (Exception ex)
        {
            attempt.Failed(ex);
            return (null, FetchFailedContentType, null, null);
        }
    }

    private static bool IsHttpUrl(string? url) =>
        url is not null &&
        (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Reads a file:// URL from the local filesystem and returns its content with detected MIME type.
    /// The file existence + binary/text read policy lives in the host
    /// <see cref="Dom.Runtime.ResourceLoader"/>; this method only maps the URL to a
    /// path and the loader's I/O exceptions to the empty-document contract.
    /// </summary>
    private (string? content, string contentType) TryReadFileResource(string fileUrl, string extensionMime)
    {
        try
        {
            var path = new Uri(fileUrl).LocalPath;
            return _resources.LoadLocalResource(path, extensionMime);
        }
        catch
        {
            return (null, string.Empty);
        }
    }

    /// <summary>
    /// Attempts to read a resource from the local base path directory.
    /// Strips query strings from the filename. Detects content type from content
    /// when extension-based detection returns a generic type (e.g. for XHTML files
    /// served without a recognized extension).
    /// </summary>
    private (string? content, string contentType) TryReadLocalResource(string resourceUrl, string extensionMime)
    {
        if (string.IsNullOrEmpty(_resources.LocalBasePath))
            return (null, string.Empty);

        // Strip query string and fragment from the URL to get the filename
        var filename = resourceUrl;
        var qIdx = filename.IndexOf('?');
        if (qIdx >= 0) filename = filename[..qIdx];
        var hIdx = filename.IndexOf('#');
        if (hIdx >= 0) filename = filename[..hIdx];

        // Only handle relative URLs (no scheme)
        if (filename.Contains("://")) return (null, string.Empty);

        var localPath = Path.Combine(_resources.LocalBasePath, filename);

        try
        {
            // The existence + binary/text read policy lives in the host loader; missing
            // → (null, ""), binary → (null, extensionMime), text → (content, extensionMime).
            var (content, detectedMime) = _resources.LoadLocalResource(localPath, extensionMime);

            // Detect content type from the read text when extension-based detection was generic.
            if (content != null &&
                (string.Equals(detectedMime, "application/octet-stream", StringComparison.OrdinalIgnoreCase)
                 || string.IsNullOrEmpty(detectedMime)))
            {
                detectedMime = DetectContentTypeFromContent(content, filename);
            }

            return (content, detectedMime);
        }
        catch
        {
            return (null, string.Empty);
        }
    }

    /// <summary>
    /// Builds a sub-document tree from fetched HTML content.
    /// </summary>
    private DomDocument BuildSubDocumentFromHtml(string html, DomElement containerElement)
    {
        var document = CreateBrowsingContextDocument();

        // Parsed into a document of its own and moved across whole: `document` is already subscribed
        // to custom-element reactions, and parsing in place would publish a record per parsed node.
        // Both children are live queries on the parsed document, so they are read before either moves.
        var parsed = HtmlDocumentParser.ParseDocument(html).Document;
        var parsedRoot = parsed.DocumentElement!;

        // The frame's DOCTYPE, before its documentElement — DOM §4.5 makes it the document's first
        // child. It is the parser's own node, so a doctype quoted in a comment is not the frame's, and
        // a SYSTEM-only or single-quoted one is.
        if (parsed.DocumentType is { } docType)
            document.AppendChild(docType);

        document.AppendChild(parsedRoot);

        LinkContentDocument(containerElement, document);

        // The pristine, pre-script shape of the frame — what its resource says. A `src` frame is
        // rendered from that file unless it moves away from this. See
        // DomBridge/Serialization.Rendering.cs.
        RecordSubDocumentSourceMarkup(document, html);

        return document;
    }

}
