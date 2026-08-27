using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>Node</c> interface constants (DOM §4.4), which every node object carries: the twelve
/// <c>*_NODE</c> type values and the six <c>DOCUMENT_POSITION_*</c> bit values.
/// </summary>
/// <remarks>
/// <para>
/// One installer for what used to be five hand-copied blocks — on the element wrapper, the three
/// non-element node wrappers, the document, and a sub-document. Each copy carried the same eight of
/// the twelve type constants, and none carried the position bits, so the omissions were identical
/// everywhere by construction and a sixth copy would have inherited them too.
/// </para>
/// <para>
/// The <c>DOCUMENT_POSITION_*</c> bits are the half that was load-bearing.
/// <c>compareDocumentPosition</c> already returned a correct DOM bitmask, but with the names absent a
/// page could not decode it: <c>result &amp; Node.DOCUMENT_POSITION_CONTAINED_BY</c> is
/// <c>result &amp; undefined</c>, which is <c>0</c> rather than an error — so a containment test did
/// not throw, it silently answered "not contained" for every pair of nodes. That is the failure mode
/// a missing constant has here, and it is why the returned bitmask being right was not enough.
/// </para>
/// <para>
/// <c>ENTITY_REFERENCE_NODE</c> (5), <c>ENTITY_NODE</c> (6) and <c>NOTATION_NODE</c> (12) are legacy
/// values that no node in a modern tree reports, but the specification still defines them on the
/// interface and the <c>Node</c> global polyfill already listed them; they are here so an instance
/// and the global agree. <c>PROCESSING_INSTRUCTION_NODE</c> (7) is not legacy — it was simply
/// missing from the instance copies.
/// </para>
/// </remarks>
internal static class NodeConstantsBinding
{
    private static readonly (string Name, int Value)[] Constants =
    [
        // Node type constants — the values Node.nodeType reports.
        ("ELEMENT_NODE", 1),
        ("ATTRIBUTE_NODE", 2),
        ("TEXT_NODE", 3),
        ("CDATA_SECTION_NODE", 4),
        ("ENTITY_REFERENCE_NODE", 5),
        ("ENTITY_NODE", 6),
        ("PROCESSING_INSTRUCTION_NODE", 7),
        ("COMMENT_NODE", 8),
        ("DOCUMENT_NODE", 9),
        ("DOCUMENT_TYPE_NODE", 10),
        ("DOCUMENT_FRAGMENT_NODE", 11),
        ("NOTATION_NODE", 12),

        // Position bits — the flags compareDocumentPosition ORs together.
        ("DOCUMENT_POSITION_DISCONNECTED", 0x01),
        ("DOCUMENT_POSITION_PRECEDING", 0x02),
        ("DOCUMENT_POSITION_FOLLOWING", 0x04),
        ("DOCUMENT_POSITION_CONTAINS", 0x08),
        ("DOCUMENT_POSITION_CONTAINED_BY", 0x10),
        ("DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC", 0x20),
    ];

    /// <summary>
    /// Adds every <c>Node</c> interface constant to a node-like JS object — for a wrapper that cannot
    /// inherit them. <c>Node.prototype</c> carries all eighteen with the same values (see
    /// <c>RegisterNodeConstructor</c>), so a wrapper whose chain reaches it needs none of its own.
    /// </summary>
    public static void Install(JSObject obj)
    {
        foreach (var (name, value) in Constants)
            obj.FastAddValue(name, new JSNumber(value), JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>The constant names, for dropping the copies a wrapper installed before it had a chain.</summary>
    public static IEnumerable<string> Names
    {
        get
        {
            foreach (var (name, _) in Constants)
                yield return name;
        }
    }
}
