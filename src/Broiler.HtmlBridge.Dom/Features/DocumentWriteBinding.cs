using System.Linq;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>document.write</c> / <c>document.writeln</c>, co-located as an HtmlBridge feature module
/// (Phase 3). <c>write</c> parses its argument as an HTML fragment and inserts the resulting nodes
/// at the parser insertion point — right after the currently executing <c>&lt;script&gt;</c>, or
/// appended to <c>&lt;body&gt;</c> as a fallback — matching real browser behaviour. <c>writeln</c>
/// is <c>write</c> with a trailing newline. The document root, element list, current-script index
/// and the fragment parser are reached through the narrow <see cref="IDocumentWriteHost"/> contract;
/// the structural moves use the bridge's neutral <c>internal static</c> tree helpers. Previously the
/// bridge's <c>JsRegistrationWrite036Core</c>/<c>JsRegistrationWriteln037Core</c> in the shared
/// JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class DocumentWriteBinding
{
    public static JSValue Write(IDocumentWriteHost host, in Arguments a)
    {
        try
        {
            if (a.Length == 0)
                return JSUndefined.Value;
            var fragment = a[0].ToString();
            var fragmentRoot = host.BuildFragment(fragment, "body");
            if (fragmentRoot.ChildNodes.Count > 0)
            {
                // Find the <body> element in the main tree.
                var mainBody = DomBridge.ChildElements(host.DocumentElement)
                    .FirstOrDefault(c => string.Equals(c.TagName, "body", StringComparison.OrdinalIgnoreCase));
                if (mainBody != null)
                {
                    // Find the currently executing <script> element so we can insert the new nodes
                    // right after it (matching real browser behaviour where document.write() inserts
                    // at the parser insertion point).
                    DomElement? currentScript = null;
                    var documentElements = host.Elements;
                    if (host.CurrentScriptIndex >= 0 && host.CurrentScriptIndex < documentElements.Count)
                    {
                        currentScript = documentElements[host.CurrentScriptIndex];
                        // Verify it's a <script> in mainBody.
                        if (DomBridge.ParentEl(currentScript) != mainBody)
                            currentScript = null;
                    }

                    var writtenChildren = fragmentRoot.ChildNodes.ToArray();
                    if (currentScript != null)
                    {
                        var insertIdx = DomBridge.ChildIndexOf(mainBody, currentScript) + 1;
                        for (int ci = 0; ci < writtenChildren.Length; ci++)
                        {
                            // Single canonical move out of the parsed fragment into mainBody at the
                            // insert position (prior SetParent-append + reposition fired spurious records).
                            DomBridge.InsertChildAt(mainBody, insertIdx + ci, writtenChildren[ci]);
                        }
                    }
                    else
                    {
                        // Fallback: append to end (single canonical move per child).
                        foreach (var child in writtenChildren)
                        {
                            mainBody.AppendChild(child);
                        }
                    }
                }
            }

            return JSUndefined.Value;
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.document.write", $"Error in document.write: {ex.Message}", ex);
            return JSUndefined.Value;
        }
    }

    public static JSValue Writeln(JSFunction? writeFn, in Arguments a)
    {
        var text = a.Length > 0 ? a[0].ToString() + "\n" : "\n";
        return writeFn.InvokeFunction(new Arguments(writeFn, new JSString(text)));
    }
}
