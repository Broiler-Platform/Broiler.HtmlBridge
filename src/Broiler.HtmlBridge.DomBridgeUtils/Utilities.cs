using System.Text;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Returns the MIME type for a given file extension.
    /// </summary>
    internal static string GetMimeTypeForExtension(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "application/octet-stream";
        var path = url;
        var qIndex = path.IndexOf('?');
        if (qIndex >= 0) path = path.Substring(0, qIndex);
        var hIndex = path.IndexOf('#');
        if (hIndex >= 0) path = path.Substring(0, hIndex);

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" or ".mjs" => "application/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".txt" or ".text" => "text/plain",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".ico" => "image/x-icon",
            ".bmp" => "image/bmp",
            ".pdf" => "application/pdf",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".mp3" => "audio/mpeg",
            ".mp4" => "video/mp4",
            ".wav" => "audio/wav",
            ".ogg" => "audio/ogg",
            ".webm" => "video/webm",
            _ => "application/octet-stream",
        };
    }

    /// <summary>
    /// Decodes a <c>data:</c> URI and returns the MIME type and decoded body content.
    /// Supports percent-encoded and base64-encoded payloads, as well as nested data URIs.
    /// </summary>
    internal static (string mimeType, string body) DecodeDataUriParts(string dataUri)
    {
        if (!dataUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return (string.Empty, string.Empty);

        var rest = dataUri[5..]; // strip "data:"
        var commaIdx = rest.IndexOf(',');
        if (commaIdx < 0)
            return (string.Empty, string.Empty);

        var meta = rest[..commaIdx]; // e.g. "text/html;base64" or "text/html;charset=utf-8"
        var payload = rest[(commaIdx + 1)..];

        // Extract MIME type (before any semicolons)
        var mimeType = meta;
        var semiIdx = meta.IndexOf(';');
        if (semiIdx >= 0)
            mimeType = meta[..semiIdx];
        if (string.IsNullOrEmpty(mimeType))
            mimeType = "text/plain"; // default per RFC 2397

        string body;
        if (meta.Contains("base64", StringComparison.OrdinalIgnoreCase))
        {
            var decoded = Uri.UnescapeDataString(payload);
            // Strip whitespace (RFC 2045 allows folding)
            decoded = System.Text.RegularExpressions.Regex.Replace(decoded, @"\s", string.Empty);
            try
            {
                var bytes = Convert.FromBase64String(decoded);
                body = Encoding.UTF8.GetString(bytes);
            }
            catch (FormatException)
            {
                // Malformed base64 payload — return empty body so the caller
                // falls back to the default empty-document path.
                body = string.Empty;
            }
        }
        else
        {
            body = Uri.UnescapeDataString(payload);
        }

        return (mimeType.Trim(), body);
    }

    /// <summary>
    /// Returns <c>true</c> if the target URL is cross-origin relative to the page URL.
    /// Relative URLs and file:// URLs are treated as same-origin.
    /// </summary>
    internal static bool IsCrossOrigin(string targetUrl, string pageUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl)) return false;
        // about:blank inherits the origin of the embedding document (always same-origin)
        if (string.Equals(targetUrl, "about:blank", StringComparison.OrdinalIgnoreCase)) return false;
        // data: URIs inherit the origin of the embedding document (always same-origin)
        if (targetUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return false;
        // Relative URLs are always same-origin
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)) return false;
        // file:// URLs are same-origin with each other
        if (string.Equals(targetUri.Scheme, "file", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(pageUrl)) return false;
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri)) return false;
        // Same-origin: same scheme + host + port (shared origin primitive)
        return !Scripting.Origin.SchemeHostPortEquals(targetUri, pageUri);
    }

    /// <summary>
    /// The canonical <see cref="DomDocument"/> that owns <paramref name="node"/>. For a connected node
    /// this is the absolute tree root: a (sub-)document root is a canonical <see cref="DomDocument"/>,
    /// so ownership is derived from tree position — no parallel <c>OwnerDocRoot</c> field. A detached
    /// node falls back to the canonical owner-document set at construction/adoption (a sub-document's
    /// <c>createElement</c> node was adopted into its content document; every other node was minted
    /// from the main <c>_document</c>).
    /// </summary>
    internal static DomDocument GetOwningDocument(DomNode node) =>
        // The absolute-root walk is canonical DomNode.GetRootNode() (the identical `while ParentNode`
        // climb); a connected node roots to its DomDocument, a detached one falls back to the
        // canonical owner-document set at construction/adoption.
        node.GetRootNode() as DomDocument ?? node.OwnerDocument;
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Throws a proper <c>DOMException</c> with the given name/code via the JS-registered constructor,
    /// so that JS try/catch blocks intercept it with full <c>.code</c>, <c>.name</c> and
    /// <c>.message</c> properties intact.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The argument order differs from the contract's, and that is the one hazard here.</b> This
    /// takes <c>(message, name)</c>, the order its call sites were written in; the contract takes
    /// <c>(name, message)</c>. Keeping this wrapper rather than inlining the contract call at each
    /// site is what stops the two being transposed five times.
    /// </para>
    /// </remarks>
    internal static void ThrowDOMException(IJsRealm realm, string message, string name) =>
        throw realm.DomError(name, message);

    /// <summary>
    /// Validates an element/doctype name per the XML spec, marshalling a canonical
    /// <see cref="DomException"/> (InvalidCharacterError) into a JavaScript <c>DOMException</c>.
    /// The validation algorithm is owned by <see cref="DomNameValidation.ValidateElementName"/>.
    /// </summary>
    internal static void ValidateElementName(string name, IJsRealm realm)
    {
        try
        {
            DomNameValidation.ValidateElementName(name);
        }
        catch (DomException ex)
        {
            ThrowDOMException(realm, ex.Message, ex.Name);
        }
    }

    /// <summary>
    /// Validates a qualified name and namespace per the Namespaces in XML spec, marshalling a
    /// canonical <see cref="DomException"/> (NamespaceError / InvalidCharacterError) into a
    /// JavaScript <c>DOMException</c>. The validation algorithm is owned by
    /// <see cref="DomNameValidation.ValidateQualifiedName"/>.
    /// </summary>
    internal static void ValidateQualifiedName(string qualifiedName, string? ns, IJsRealm realm)
    {
        try
        {
            DomNameValidation.ValidateQualifiedName(qualifiedName, ns);
        }
        catch (DomException ex)
        {
            ThrowDOMException(realm, ex.Message, ex.Name);
        }
    }

    /// <summary>
    /// Registers the <c>DOMException</c> constructor on <paramref name="realm"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Internal rather than private because a worker's realm needs it too: without it
    /// <see cref="ThrowDOMException"/> falls back to throwing a bare string, so worker code catching
    /// a <c>NetworkError</c> or <c>DataCloneError</c> would find no <c>.name</c> or <c>.code</c> to
    /// branch on. See <c>JSWorker.InstallWorkerGlobals</c>, which is why the parameter is the realm
    /// rather than this bridge's own: a worker's realm is a different one on a different thread.
    /// </para>
    /// <para>
    /// Host script — the text below is a compile-time constant of this assembly and is not subject to
    /// the page's content policy, which is the distinction <c>IJsSource</c> draws.
    /// The label is new and diagnostics-only: the engine's bare <c>Eval</c> carried none, and a named
    /// frame is what a stack trace through this constructor now says instead of nothing.
    /// </para>
    /// </remarks>
    internal static void RegisterDOMException(IJsRealm realm)
    {
        realm.EvaluateHostScript(@"
            function DOMException(message, name) {
                this.message = message || '';
                this.name = name || 'Error';
                // Map name to legacy code
                var codeMap = {
                    'IndexSizeError': 1,
                    'DOMStringSizeError': 2,
                    'HierarchyRequestError': 3,
                    'WrongDocumentError': 4,
                    'InvalidCharacterError': 5,
                    'NoDataAllowedError': 6,
                    'NoModificationAllowedError': 7,
                    'NotFoundError': 8,
                    'NotSupportedError': 9,
                    'InUseAttributeError': 10,
                    'InvalidStateError': 11,
                    'SyntaxError': 12,
                    'InvalidModificationError': 13,
                    'NamespaceError': 14,
                    'InvalidAccessError': 15,
                    'TypeMismatchError': 17,
                    'SecurityError': 18,
                    'NetworkError': 19,
                    'AbortError': 20,
                    'URLMismatchError': 21,
                    'QuotaExceededError': 22,
                    'TimeoutError': 23,
                    'InvalidNodeTypeError': 24,
                    'DataCloneError': 25
                };
                this.code = codeMap[this.name] || 0;
            }
            DOMException.INDEX_SIZE_ERR = 1;
            DOMException.DOMSTRING_SIZE_ERR = 2;
            DOMException.HIERARCHY_REQUEST_ERR = 3;
            DOMException.WRONG_DOCUMENT_ERR = 4;
            DOMException.INVALID_CHARACTER_ERR = 5;
            DOMException.NO_DATA_ALLOWED_ERR = 6;
            DOMException.NO_MODIFICATION_ALLOWED_ERR = 7;
            DOMException.NOT_FOUND_ERR = 8;
            DOMException.NOT_SUPPORTED_ERR = 9;
            DOMException.INUSE_ATTRIBUTE_ERR = 10;
            DOMException.INVALID_STATE_ERR = 11;
            DOMException.SYNTAX_ERR = 12;
            DOMException.INVALID_MODIFICATION_ERR = 13;
            DOMException.NAMESPACE_ERR = 14;
            DOMException.INVALID_ACCESS_ERR = 15;
            DOMException.TYPE_MISMATCH_ERR = 17;
            DOMException.SECURITY_ERR = 18;
            DOMException.NETWORK_ERR = 19;
            DOMException.ABORT_ERR = 20;
            DOMException.URL_MISMATCH_ERR = 21;
            DOMException.QUOTA_EXCEEDED_ERR = 22;
            DOMException.TIMEOUT_ERR = 23;
            DOMException.INVALID_NODE_TYPE_ERR = 24;
            DOMException.DATA_CLONE_ERR = 25;
            DOMException.prototype = Object.create(Error.prototype);
            DOMException.prototype.constructor = DOMException;
            DOMException.prototype.INDEX_SIZE_ERR = 1;
            DOMException.prototype.DOMSTRING_SIZE_ERR = 2;
            DOMException.prototype.HIERARCHY_REQUEST_ERR = 3;
            DOMException.prototype.WRONG_DOCUMENT_ERR = 4;
            DOMException.prototype.INVALID_CHARACTER_ERR = 5;
            DOMException.prototype.NO_DATA_ALLOWED_ERR = 6;
            DOMException.prototype.NO_MODIFICATION_ALLOWED_ERR = 7;
            DOMException.prototype.NOT_FOUND_ERR = 8;
            DOMException.prototype.NOT_SUPPORTED_ERR = 9;
            DOMException.prototype.INUSE_ATTRIBUTE_ERR = 10;
            DOMException.prototype.INVALID_STATE_ERR = 11;
            DOMException.prototype.SYNTAX_ERR = 12;
            DOMException.prototype.INVALID_MODIFICATION_ERR = 13;
            DOMException.prototype.NAMESPACE_ERR = 14;
            DOMException.prototype.INVALID_ACCESS_ERR = 15;
            DOMException.prototype.TYPE_MISMATCH_ERR = 17;
            DOMException.prototype.SECURITY_ERR = 18;
            DOMException.prototype.NETWORK_ERR = 19;
            DOMException.prototype.ABORT_ERR = 20;
            DOMException.prototype.URL_MISMATCH_ERR = 21;
            DOMException.prototype.QUOTA_EXCEEDED_ERR = 22;
            DOMException.prototype.TIMEOUT_ERR = 23;
            DOMException.prototype.INVALID_NODE_TYPE_ERR = 24;
            DOMException.prototype.DATA_CLONE_ERR = 25;
        ", "polyfill:dom-exception");
    }

    /// <summary>
    /// Registers the <c>Node</c> constructor with DOM type constants on the realm.
    /// </summary>
    internal static void RegisterNodeConstructor(IJsRealm realm)
    {
        realm.EvaluateHostScript(@"
            function Node() {}
            Node.ELEMENT_NODE = 1;
            Node.ATTRIBUTE_NODE = 2;
            Node.TEXT_NODE = 3;
            Node.CDATA_SECTION_NODE = 4;
            Node.ENTITY_REFERENCE_NODE = 5;
            Node.ENTITY_NODE = 6;
            Node.PROCESSING_INSTRUCTION_NODE = 7;
            Node.COMMENT_NODE = 8;
            Node.DOCUMENT_NODE = 9;
            Node.DOCUMENT_TYPE_NODE = 10;
            Node.DOCUMENT_FRAGMENT_NODE = 11;
            Node.NOTATION_NODE = 12;
            Node.prototype.ELEMENT_NODE = 1;
            Node.prototype.ATTRIBUTE_NODE = 2;
            Node.prototype.TEXT_NODE = 3;
            Node.prototype.CDATA_SECTION_NODE = 4;
            Node.prototype.ENTITY_REFERENCE_NODE = 5;
            Node.prototype.ENTITY_NODE = 6;
            Node.prototype.PROCESSING_INSTRUCTION_NODE = 7;
            Node.prototype.COMMENT_NODE = 8;
            Node.prototype.DOCUMENT_NODE = 9;
            Node.prototype.DOCUMENT_TYPE_NODE = 10;
            Node.prototype.DOCUMENT_FRAGMENT_NODE = 11;
            Node.prototype.NOTATION_NODE = 12;
            // The bits compareDocumentPosition ORs together (DOM 4.4). Without them,
            // result & Node.DOCUMENT_POSITION_CONTAINED_BY is result & undefined, which is 0
            // rather than an error - so a containment test silently reported no containment for
            // every pair of nodes even though the bitmask coming back was correct.
            Node.DOCUMENT_POSITION_DISCONNECTED = 0x01;
            Node.DOCUMENT_POSITION_PRECEDING = 0x02;
            Node.DOCUMENT_POSITION_FOLLOWING = 0x04;
            Node.DOCUMENT_POSITION_CONTAINS = 0x08;
            Node.DOCUMENT_POSITION_CONTAINED_BY = 0x10;
            Node.DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC = 0x20;
            Node.prototype.DOCUMENT_POSITION_DISCONNECTED = 0x01;
            Node.prototype.DOCUMENT_POSITION_PRECEDING = 0x02;
            Node.prototype.DOCUMENT_POSITION_FOLLOWING = 0x04;
            Node.prototype.DOCUMENT_POSITION_CONTAINS = 0x08;
            Node.prototype.DOCUMENT_POSITION_CONTAINED_BY = 0x10;
            Node.prototype.DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC = 0x20;
        ", "polyfill:node-constants");
    }

    internal static void RegisterSVGLength(IJsRealm realm)
    {
        realm.EvaluateHostScript(@"
            function SVGLength() {}
            SVGLength.SVG_LENGTHTYPE_UNKNOWN = 0;
            SVGLength.SVG_LENGTHTYPE_NUMBER = 1;
            SVGLength.SVG_LENGTHTYPE_PERCENTAGE = 2;
            SVGLength.SVG_LENGTHTYPE_EMS = 3;
            SVGLength.SVG_LENGTHTYPE_EXS = 4;
            SVGLength.SVG_LENGTHTYPE_PX = 5;
            SVGLength.SVG_LENGTHTYPE_CM = 6;
            SVGLength.SVG_LENGTHTYPE_MM = 7;
            SVGLength.SVG_LENGTHTYPE_IN = 8;
            SVGLength.SVG_LENGTHTYPE_PT = 9;
            SVGLength.SVG_LENGTHTYPE_PC = 10;
        ", "polyfill:svg-length");
    }
}

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Whether the control is disabled — by its own <c>disabled</c> attribute or by an ancestor
    /// <c>&lt;fieldset disabled&gt;</c>, which disables everything in it (HTML §4.10.15).
    /// </summary>
    internal static bool IsFormControlDisabled(DomElement control) =>
        Broiler.Dom.Html.HtmlFormQueries.IsFormControlDisabled(control);

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

public static partial class DomBridgeUtils
{
    internal const string FormSubmitLogContext = "DomBridge.submit";
}
