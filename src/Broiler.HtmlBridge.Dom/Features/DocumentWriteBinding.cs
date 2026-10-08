using System.Linq;
using Broiler.Dom;
using Broiler.Dom.Html;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>document.write</c> / <c>document.writeln</c>, co-located as an HtmlBridge feature module.
/// <c>write</c> parses its argument as an HTML fragment and inserts the resulting nodes
/// at the parser insertion point — right after the currently executing <c>&lt;script&gt;</c>, or
/// appended to <c>&lt;body&gt;</c> as a fallback — matching real browser behaviour. <c>writeln</c>
/// is <c>write</c> with a trailing newline. The document root, element list and current-script index
/// are reached through the narrow <see cref="IDocumentWriteHost"/> contract; the fragment is parsed by
/// the shared <see cref="HtmlDocumentParser"/>, and the structural moves use the bridge's neutral
/// <c>internal static</c> tree helpers.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>). <see cref="IDocumentWriteHost"/>
/// names no engine type, so the whole of this unit's coupling is the two argument reads and
/// <c>writeln</c>'s re-entry into <c>write</c>.
/// </remarks>
internal static class DocumentWriteBinding
{
    private sealed class HostWriteState
    {
        public int ScriptIndex { get; set; } = -1;
        public DomElement? CurrentScript { get; set; }
        public DomNode? LastInsertedNode { get; set; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IDocumentWriteHost, HostWriteState> HostStates = new();

    public static JsValue Write(IDocumentWriteHost host, in JsCall call)
    {
        try
        {
            if (call.Length == 0)
                return JsValue.Undefined;

            // ToJsString, not the handle's rendering: document.write of an object has always run the
            // object's own toString, and what a page writes is what that returns. When multiple
            // arguments are passed, concatenate them matching standard document.write(...text).
            string fragment;
            if (call.Length == 1)
            {
                fragment = call.Realm.ToJsString(call[0]);
            }
            else
            {
                var sb = new System.Text.StringBuilder();
                for (var ai = 0; ai < call.Length; ai++)
                    sb.Append(call.Realm.ToJsString(call[ai]));
                fragment = sb.ToString();
            }

            var state = HostStates.GetOrCreateValue(host);
            if (state.ScriptIndex != host.CurrentScriptIndex)
            {
                state.ScriptIndex = host.CurrentScriptIndex;
                var documentElements = host.Elements;
                state.CurrentScript = (host.CurrentScriptIndex >= 0 && host.CurrentScriptIndex < documentElements.Count)
                    ? documentElements[host.CurrentScriptIndex]
                    : null;
                state.LastInsertedNode = null;
            }

            // Find the currently executing <script> element so we can insert the new nodes
            // right after it (matching real browser behaviour where document.write() inserts
            // at the parser insertion point).
            var currentScript = (host.CurrentScriptIndex >= 0 && state.CurrentScript != null)
                ? state.CurrentScript
                : null;

            var scriptParent = currentScript != null ? DomBridgeUtils.ParentEl(currentScript) : null;

            // Find the <body> element in the main tree as fallback when no script or parent is available.
            var mainBody = DomBridgeUtils.ChildElements(host.DocumentElement)
                .FirstOrDefault(c => string.Equals(c.TagName, "body", StringComparison.OrdinalIgnoreCase));

            var targetParent = scriptParent ?? mainBody;
            if (targetParent == null)
                return JsValue.Undefined;

            // The parser's fragment belongs to a private document, so its nodes are adopted, and a
            // defined custom element among them upgraded, only as they are inserted below.
            // Parse in the context of the script's parent element (e.g. td, th, head, div) so elements
            // valid only in specific contexts survive, falling back to body.
            var contextTag = scriptParent != null && !string.IsNullOrWhiteSpace(scriptParent.TagName)
                ? scriptParent.TagName.ToLowerInvariant()
                : "body";

            DomDocumentFragment fragmentRoot;
            try
            {
                fragmentRoot = HtmlDocumentParser.ParseFragment(fragment, contextTag).Fragment;
            }
            catch
            {
                fragmentRoot = HtmlDocumentParser.ParseFragment(fragment, "body").Fragment;
            }

            if (fragmentRoot.ChildNodes.Count == 0)
                return JsValue.Undefined;

            var writtenChildren = fragmentRoot.ChildNodes.ToArray();

            // Determine insertion point: consecutive writes within the same script insert right
            // after the last inserted node to preserve document order; the initial write inserts
            // right after the executing script element.
            int insertIdx = -1;
            if (state.LastInsertedNode != null && ReferenceEquals(state.LastInsertedNode.ParentNode, targetParent))
            {
                var lastIdx = DomBridgeUtils.ChildIndexOf(targetParent, state.LastInsertedNode);
                if (lastIdx >= 0)
                {
                    insertIdx = lastIdx + 1;
                }
            }

            if (insertIdx < 0 && currentScript != null && ReferenceEquals(currentScript.ParentNode, targetParent))
            {
                var scriptIdx = DomBridgeUtils.ChildIndexOf(targetParent, currentScript);
                if (scriptIdx >= 0)
                {
                    insertIdx = scriptIdx + 1;
                }
            }

            if (insertIdx >= 0)
            {
                for (int ci = 0; ci < writtenChildren.Length; ci++)
                {
                    // Single canonical move out of the parsed fragment into targetParent at the
                    // insert position (prior SetParent-append + reposition fired spurious records).
                    DomBridgeUtils.InsertChildAt(targetParent, insertIdx + ci, writtenChildren[ci]);
                }
            }
            else
            {
                // Fallback: append to end of targetParent (single canonical move per child).
                foreach (var child in writtenChildren)
                {
                    targetParent.AppendChild(child);
                }
            }

            if (writtenChildren.Length > 0)
            {
                state.LastInsertedNode = writtenChildren[^1];
            }

            return JsValue.Undefined;
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.document.write", $"Error in document.write: {ex.Message}", ex);
            return JsValue.Undefined;
        }
    }

    /// <summary>
    /// <c>document.writeln(text)</c> — <c>write</c> with a trailing newline, delegated to the very
    /// function <c>document.write</c> is, so a page that replaces neither sees one implementation.
    /// </summary>
    /// <remarks>
    /// The receiver of the inner call is the write function itself. That is not what a browser passes
    /// (it would be the document), and it is what this call site has always passed — <c>write</c>
    /// reads nothing off <c>this</c>, so the two are indistinguishable to a page, and correcting it
    /// belongs in its own change.
    /// </remarks>
    public static JsValue Writeln(JsValue writeFunction, in JsCall call)
    {
        var text = call.Length > 0 ? call.Realm.ToJsString(call[0]) + "\n" : "\n";
        return call.Realm.Invoke(writeFunction, writeFunction, [JsValue.String(text)]);
    }
}
