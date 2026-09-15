using System.Globalization;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    /// <summary>
    /// One <c>check-layout-th.js</c> geometry assertion evaluated against the
    /// bridge's computed box metrics: the <c>data-*</c> attribute's expected
    /// value alongside the value the bridge computes for the same element.
    /// </summary>
    /// <param name="Element">Human-readable element descriptor (e.g. <c>span.abspos[title=start]</c>).</param>
    /// <param name="Property">The checked geometry property (e.g. <c>offset-y</c>, <c>width</c>).</param>
    /// <param name="Expected">The value declared by the test's <c>data-*</c> attribute.</param>
    /// <param name="Actual">The value the bridge's layout-metrics estimator computes.</param>
    public readonly record struct CheckLayoutAssertion(string Element, string Property, double Expected, double Actual);

    /// <summary>
    /// Evaluates the <c>data-offset-*</c> / <c>data-expected-*</c> /
    /// <c>data-total-*</c> assertions that <c>check-layout-th.js</c> tests carry,
    /// comparing each against the bridge's computed box geometry — the same metrics
    /// (<c>offsetTop</c>, <c>offsetWidth</c>, …) the test's own JavaScript would read.
    /// Returns one entry per declared assertion (the caller decides pass/fail with a
    /// tolerance). The runner uses this to turn an opaque pixel mismatch on a
    /// check-layout test into a precise, font-independent "<c>expected offset-y=15,
    /// got 0</c>" diagnostic.
    /// </summary>
    public IReadOnlyList<CheckLayoutAssertion> EvaluateCheckLayoutAssertions()
    {
        // The whole pass reads one static post-script layout snapshot, so the
        // box-geometry estimators can be memoized for its duration — without this
        // a deep tree's offset queries are exponential and time out (WPT #1113).
        return WithLayoutGeometryCache(() =>
        {
            var results = new List<CheckLayoutAssertion>();
            if (DocumentElement is { } root)
                CollectCheckLayoutAssertions(root, results);
            return results;
        });
    }

    private void CollectCheckLayoutAssertions(Broiler.Dom.DomElement element, List<CheckLayoutAssertion> results)
    {
        foreach (var (attribute, property) in CheckLayoutAttributeMap)
        {
            if (TryGetAttribute(element, attribute, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected))
            {
                results.Add(new CheckLayoutAssertion(DescribeElement(element), property, expected, ComputeCheckLayoutMetric(element, property)));
            }
        }

        foreach (var child in ChildElements(element))
            CollectCheckLayoutAssertions(child, results);
    }

    private double ComputeCheckLayoutMetric(Broiler.Dom.DomElement element, string property)
    {
        var isRoot = IsViewportElementForMetrics(element);
        return property switch
        {
            "offset-x" => GetOffsetLeftForDomElement(element),
            "offset-y" => GetOffsetTopForDomElement(element),
            "width" => GetOffsetWidthForDomElement(element, isRoot),
            "height" => GetOffsetHeightForDomElement(element, isRoot),
            "client-width" => GetClientWidthForDomElement(element, isRoot),
            "client-height" => GetClientHeightForDomElement(element, isRoot),
            // check-layout's data-total-{x,y} is offset + border-box size on that axis.
            "total-x" => GetOffsetLeftForDomElement(element) + GetOffsetWidthForDomElement(element, isRoot),
            "total-y" => GetOffsetTopForDomElement(element) + GetOffsetHeightForDomElement(element, isRoot),
            _ => double.NaN,
        };
    }
}
