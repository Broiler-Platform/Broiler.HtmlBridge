using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>Utilities.cs</c> (Phase 3 ratchet, 2026-07-17) to keep it
/// under the 750-line guard: the cohesive DOM element/qualified-name validation cluster together
/// with the JS-side constructor globals it validates against — the <c>DOMException</c> constructor
/// (and the C# helper that throws it), plus the <c>Node</c> and <c>SVGLength</c> constant carriers.
/// The spec name-validation algorithm itself now lives in the canonical
/// <see cref="DomNameValidation"/> (Broiler.Dom); the bridge only marshals the thrown
/// <see cref="DomException"/> into a JavaScript <c>DOMException</c>.
/// </summary>
public sealed partial class DomBridge
{
    // ------------------------------------------------------------------
    //  Element name validation
    // ------------------------------------------------------------------

    /// <summary>
    /// Throws a proper <c>DOMException</c> with the given name/code via the JS-registered constructor.
    /// Constructs the DOMException object in C# and throws it as a <see cref="JSException"/>
    /// so that JS try/catch blocks can intercept it with full <c>.code</c>, <c>.name</c>,
    /// and <c>.message</c> properties intact.
    /// </summary>
    internal static void ThrowDOMException(JSContext context, string message, string name)
    {
        if (context["DOMException"] is JSFunction domExCtor)
        {
            var exObj = domExCtor.CreateInstance(
                new Arguments(domExCtor, new JSString(message), new JSString(name)));
            throw new JSException(exObj);
        }

        // Fallback when DOMException constructor is unavailable
        throw new JSException(new JSString($"DOMException: {message} ({name})"));
    }

    /// <summary>
    /// Validates an element/doctype name per the XML spec, marshalling a canonical
    /// <see cref="DomException"/> (InvalidCharacterError) into a JavaScript <c>DOMException</c>.
    /// The validation algorithm is owned by <see cref="DomNameValidation.ValidateElementName"/>.
    /// </summary>
    internal static void ValidateElementName(string name, JSContext context)
    {
        try
        {
            DomNameValidation.ValidateElementName(name);
        }
        catch (DomException ex)
        {
            ThrowDOMException(context, ex.Message, ex.Name);
        }
    }

    /// <summary>
    /// Validates a qualified name and namespace per the Namespaces in XML spec, marshalling a
    /// canonical <see cref="DomException"/> (NamespaceError / InvalidCharacterError) into a
    /// JavaScript <c>DOMException</c>. The validation algorithm is owned by
    /// <see cref="DomNameValidation.ValidateQualifiedName"/>.
    /// </summary>
    internal static void ValidateQualifiedName(string qualifiedName, string? ns, JSContext context)
    {
        try
        {
            DomNameValidation.ValidateQualifiedName(qualifiedName, ns);
        }
        catch (DomException ex)
        {
            ThrowDOMException(context, ex.Message, ex.Name);
        }
    }

    /// <summary>
    /// Validates a <c>setAttribute</c>/<c>toggleAttribute</c> attribute name (DOM §4.9.1), throwing
    /// <c>InvalidCharacterError</c> when it does not match the XML <c>Name</c> production.
    /// </summary>
    /// <remarks>
    /// A separate rule from <see cref="ValidateElementName"/> rather than a reuse of it, because
    /// <c>Name</c> allows colons and the element-name pattern deliberately does not — see
    /// <see cref="Dom.Features.DomApiSyntax.IsValidAttributeName"/> for why that distinction is the
    /// load-bearing one. Every call site is a scripted DOM entry point: the canonical
    /// <c>DomElement.SetAttribute</c> stays permissive because the HTML parser goes through it.
    /// </remarks>
    internal static void ValidateAttributeName(string name, JSContext? context)
    {
        if (context is not null && !Dom.Features.DomApiSyntax.IsValidAttributeName(name))
        {
            ThrowDOMException(
                context,
                $"Failed to execute 'setAttribute' on 'Element': '{name}' is not a valid attribute name.",
                "InvalidCharacterError");
        }
    }

    /// <summary>
    /// Validates a selector argument (DOM §4.2.6), throwing <c>SyntaxError</c> when it does not parse
    /// as a selector list.
    /// </summary>
    /// <remarks>
    /// Shared by all five scripted entry points that take one — <c>querySelector</c>,
    /// <c>querySelectorAll</c>, <c>matches</c> and <c>closest</c> on an element, the two document
    /// forms, the sub-document forms, and the <c>DocumentFragment</c> forms — because a browser throws
    /// from all of them identically, which was measured rather than assumed. The CSS cascade does not
    /// come through here and stays lenient, as CSS error handling requires.
    /// </remarks>
    internal static void ValidateSelector(string selector, JSContext? context)
    {
        if (context is not null && !Dom.Features.DomApiSyntax.IsValidSelectorList(selector))
        {
            ThrowDOMException(
                context,
                $"Failed to execute 'querySelector' on 'Document': '{selector}' is not a valid selector.",
                "SyntaxError");
        }
    }

    /// <summary>
    /// Registers the <c>DOMException</c> constructor on <paramref name="context"/>.
    /// </summary>
    /// <remarks>
    /// Internal rather than private because a worker's context needs it too: without it
    /// <see cref="ThrowDOMException"/> falls back to throwing a bare string, so worker code catching
    /// a <c>NetworkError</c> or <c>DataCloneError</c> would find no <c>.name</c> or <c>.code</c> to
    /// branch on. See <c>JSWorker.InstallWorkerGlobals</c>.
    /// </remarks>
    internal static void RegisterDOMException(JSContext context)
    {
        context.Eval(@"
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
        ");
    }

    /// <summary>
    /// Registers the <c>Node</c> constructor with DOM type constants on the JS context.
    /// </summary>
    private static void RegisterNodeConstructor(JSContext context)
    {
        context.Eval(@"
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
        ");
    }

    private static void RegisterSVGLength(JSContext context)
    {
        context.Eval(@"
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
        ");
    }
}
