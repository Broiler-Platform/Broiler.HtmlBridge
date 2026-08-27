using System;
using System.Collections.Generic;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// Which <c>&lt;script&gt;</c> element in the parsed document produced the <em>n</em>-th entry of an
/// execution bucket — the mapping a host needs to say which script is running.
/// </summary>
/// <remarks>
/// <para>
/// A host holds two things that look parallel and are not: a list of program texts to evaluate, and
/// the document's <c>&lt;script&gt;</c> elements. The bucket lists hold only what actually executes,
/// while the element list holds every <c>&lt;script&gt;</c> the parser built — so pairing them by
/// position is only correct on a document where the two sets coincide. Every host paired them that
/// way, and so on any page carrying a data block the <em>n</em>-th executed script was attributed to
/// the wrong element: on <c>about.google</c>, whose first <c>&lt;script&gt;</c> is a JSON-LD block,
/// the first script that ran was attributed to the JSON-LD.
/// </para>
/// <para>
/// What that attribution feeds is <c>document.currentScript</c> and the <c>document.write</c>
/// insertion point, both of which are about <em>which element</em>, so a wrong answer there is worse
/// than none: <c>document.currentScript.src</c> reads the JSON-LD's absent <c>src</c> instead of the
/// running script's URL.
/// </para>
/// <para>
/// The classification here is deliberately the one the extractors already apply when they fill the
/// buckets — a data block (a <c>type</c> that is neither a JavaScript MIME essence nor
/// <c>module</c>) is not executed at all, a module belongs to neither classic bucket, and
/// <c>defer</c> decides between the two, ahead of <c>async</c> and regardless of whether the element
/// has a <c>src</c>. Matching the extractors is what makes the lists line up; where the extractors
/// depart from the HTML spec's own reading of <c>defer</c>, this departs with them.
/// </para>
/// <para>
/// <b>Two cases stay approximate for a host that pairs against <see cref="Classic"/> or
/// <see cref="Deferred"/>,</b> and both degrade to naming a neighbouring script rather than a
/// non-script: a source the host could not resolve (blocked by CSP, or a fetch that failed) is
/// absent from the bucket but present here, and a host that hoists its <c>async</c> scripts to the
/// end of the classic bucket rather than leaving them in document order pairs them differently from
/// this list. Neither is knowable from the parsed elements alone, which is the point —
/// reconstructing the host's classification can only be as good as the two readings agreeing.
/// </para>
/// <para>
/// <see cref="AllInDocumentOrder"/> is the way out, and the capture host uses it: it records which
/// <c>&lt;script&gt;</c> each bucket entry came from as that element's ordinal, so there is no
/// classification to reproduce and neither case arises. A host that still pairs against the two
/// classified lists keeps both approximations.
/// </para>
/// </remarks>
public static class ScriptElementMap
{
    /// <summary>
    /// Indices into <paramref name="elements"/> of the <c>&lt;script&gt;</c> elements a host's
    /// classic (non-deferred) bucket executes, in document order.
    /// </summary>
    public static IReadOnlyList<int> Classic(IReadOnlyList<DomElement> elements) => Collect(elements, deferred: false);

    /// <summary>
    /// Indices into <paramref name="elements"/> of the <c>&lt;script&gt;</c> elements a host's
    /// deferred bucket executes, in document order.
    /// </summary>
    public static IReadOnlyList<int> Deferred(IReadOnlyList<DomElement> elements) => Collect(elements, deferred: true);

    /// <summary>
    /// Indices into <paramref name="elements"/> of <em>every</em> <c>&lt;script&gt;</c> element, in
    /// document order and with no classification at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a host should pair its buckets against, and the reason is that it needs to agree
    /// with the host about nothing. <see cref="Classic"/> and <see cref="Deferred"/> reproduce the
    /// extractors' classification from the parsed elements, which works only while the two readings
    /// stay identical — and they cannot, for the two cases named above: a source the host could not
    /// resolve is absent from its bucket and present in that list, and every later pairing shifts by
    /// one. A host that instead records which <c>&lt;script&gt;</c> each bucket entry came from —
    /// its ordinal among all of them, which is what this list is indexed by — has nothing to
    /// reproduce and nothing to drift from.
    /// </para>
    /// <para>
    /// The ordinal is the pairing key rather than the element itself because an extractor that scans
    /// the source, as the capture host's does, counts <c>&lt;script&gt;</c> starts and never holds a
    /// <see cref="DomElement"/> to hand.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<int> AllInDocumentOrder(IReadOnlyList<DomElement> elements)
    {
        var indices = new List<int>();
        if (elements == null)
            return indices;

        for (var i = 0; i < elements.Count; i++)
        {
            if (string.Equals(elements[i].TagName, "script", StringComparison.OrdinalIgnoreCase))
                indices.Add(i);
        }

        return indices;
    }

    private static List<int> Collect(IReadOnlyList<DomElement> elements, bool deferred)
    {
        var indices = new List<int>();
        if (elements == null)
            return indices;

        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (!string.Equals(element.TagName, "script", StringComparison.OrdinalIgnoreCase))
                continue;

            var type = element.GetAttribute("type");
            if (!ScriptMimeType.IsExecutable(type) || ScriptMimeType.IsModule(type))
                continue;

            if (element.HasAttribute("defer") == deferred)
                indices.Add(i);
        }

        return indices;
    }
}
