using System.Runtime.CompilerServices;
using Broiler.CSS;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The write side of one style sheet's rule model for <c>CSSStyleRule.style</c>: finds the style rule a
/// declaration was built over in the sheet's live rule list, replaces it with a rule carrying the edited
/// declaration block, and reports the edit through the same mutation signal <c>insertRule</c> and
/// <c>deleteRule</c> use.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a declaration needs this at all.</b> A rule's <c>style</c> used to be a detached property map
/// built from the rule's serialized block. Writes to it answered back through <c>rule.style</c> and
/// <c>rule.cssText</c> and reached nothing else: the list the <c>getComputedStyle</c> engine and the
/// renderer read still held the rule as parsed, so a page that restyled a component with
/// <c>rule.style.display = 'none'</c> kept it showing.
/// </para>
/// <para>
/// <b>Why the rule is replaced, not changed.</b> Nothing in the consumed <c>Broiler.CSS</c> model can be
/// changed in place — <see cref="CssStyleRule"/>, <see cref="CssDeclarationBlock"/> and
/// <see cref="CssAtRule.Rules"/> are all get-only over private copies — and it would be wrong even if it
/// could: <c>StyleSheetRuntimeState.CopyTo</c> copies the <em>list</em>, so a <c>cloneNode</c> copy of the
/// <c>&lt;style&gt;</c> and the render projection share the rule objects with the live sheet, and an
/// in-place edit would leak into both. Swapping one list entry touches only this sheet's list. A style rule
/// inside a grouping rule (<c>@media</c>, <c>@supports</c>, <c>@layer</c>, <c>@container</c> …) is reached
/// by rebuilding each ancestor at-rule around the replaced child, for the same reason.
/// </para>
/// <para>
/// <b>Why the rule is found by reference, not by index.</b> <c>insertRule</c> and <c>deleteRule</c> move a
/// rule's index after a page took its declaration, and the list also holds rules the CSSOM hides
/// (<c>StyleSheetBinding.IsCssomVisible</c>), so a CSSOM index is not a list position either. The model
/// rule object itself is the identity both lists agree on.
/// </para>
/// <para>
/// <b>Why declarations share a <see cref="RuleCell"/>.</b> The bridge builds a new rule object, and so a new
/// declaration, every time <c>cssRules</c> is read, where Chromium hands back the same one. Two
/// declarations of one rule each holding their own reference would diverge on the first write: the second
/// writer would still point at the rule the first one replaced and fail to find it, so its edit — and every
/// later one made through it — would be dropped from the cascade, while its reads went on without the first
/// writer's edit, which does stay applied. A cell per model rule, looked up from any version of that rule,
/// keeps every declaration of it on the current version. The table is per sheet rather than static because
/// a cloned <c>&lt;style&gt;</c> shares rule objects with its source but has its own list and its own edits.
/// </para>
/// <para>
/// <b>When the rule is not found</b> — it was deleted, the sheet's text was replaced and reparsed, or it was
/// inserted through a grouping rule's own <c>cssRules</c>, which never reaches the model
/// (<c>CssRuleObjectTests.Characterization_ANestedInsertLivesOnlyOnTheRuleObjectItWasMadeThrough</c>) — the
/// edit is kept in the cell, so the declaration reads its own writes, and nothing is marked: a rule that is
/// not in the sheet cannot move the cascade.
/// </para>
/// </remarks>
internal sealed class StyleSheetRuleModel(Func<List<CssRule>> currentRules, Action markRulesMutated)
{
    private readonly ConditionalWeakTable<CssStyleRule, RuleCell> _cells = [];

    /// <summary>The current version of one model style rule, shared by every declaration built over it.</summary>
    internal sealed class RuleCell(CssStyleRule current)
    {
        public CssStyleRule Current { get; set; } = current;
    }

    /// <summary>The cell for <paramref name="rule"/>, which may be any version of it this model produced.</summary>
    public RuleCell CellFor(CssStyleRule rule) => _cells.GetValue(rule, static r => new RuleCell(r));

    /// <summary>
    /// Replaces the cell's rule with one carrying <paramref name="declarations"/>, in the sheet's list when it
    /// is still there, and reports the edit when it was.
    /// </summary>
    public void Commit(RuleCell cell, CssDeclarationBlock declarations)
    {
        var old = cell.Current;
        var replacement = new CssStyleRule(old.Selectors, declarations, old.Range);

        // currentRules() reparses a pending textContent change first, so a rule from the old text is not
        // found — the CSSOM outcome for a rule no longer in the sheet.
        var found = TryReplace(currentRules(), old, replacement);

        _cells.AddOrUpdate(replacement, cell);
        cell.Current = replacement;

        // Only after the list holds the replacement: the signal invalidates computed style, and a
        // re-resolution it lets through must read the edited rule.
        if (found)
            markRulesMutated();
    }

    private static bool TryReplace(IList<CssRule> rules, CssStyleRule old, CssStyleRule replacement)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (ReferenceEquals(rules[i], old))
            {
                rules[i] = replacement;
                return true;
            }

            if (rules[i] is CssAtRule { Rules.Count: > 0 } atRule &&
                !IsKeyframes(atRule) &&
                TryRebuild(atRule, old, replacement) is { } rebuilt)
            {
                rules[i] = rebuilt;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <paramref name="atRule"/> rebuilt around its children with <paramref name="old"/> replaced at whatever
    /// depth it sits, or <see langword="null"/> when it is not among them. Everything but the children is
    /// carried over; the serializer writes an at-rule's children rather than its <c>BlockText</c> whenever it
    /// has any, so the stale block text is never read for a rule rebuilt here.
    /// </summary>
    private static CssAtRule? TryRebuild(CssAtRule atRule, CssStyleRule old, CssStyleRule replacement)
    {
        var children = atRule.Rules.ToList();
        return TryReplace(children, old, replacement)
            ? new CssAtRule(atRule.Name, atRule.Prelude, atRule.BlockText, atRule.Declarations, children, atRule.Range)
            : null;
    }

    /// <summary>
    /// A keyframe's declaration block feeds animation sampling rather than the cascade, and its declaration is
    /// never built over this model (<c>StyleSheetBinding.BuildCssKeyframeRuleObject</c>), so no rule is ever
    /// looked for inside one.
    /// </summary>
    private static bool IsKeyframes(CssAtRule atRule) =>
        atRule.Name.Equals("keyframes", StringComparison.OrdinalIgnoreCase) ||
        atRule.Name.Equals("-webkit-keyframes", StringComparison.OrdinalIgnoreCase);
}
