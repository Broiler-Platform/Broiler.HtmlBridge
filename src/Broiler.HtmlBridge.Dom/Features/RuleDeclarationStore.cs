using Broiler.CSS;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// What a rule's <c>CSSStyleDeclaration</c> (<c>rule.style</c>) reads and writes: the declared
/// properties as a property map, and the three edits CSSOM makes to them. The declaration object itself
/// (<see cref="StyleDeclarationBinding.BuildRuleDeclaration(Broiler.HtmlBridge.Jseal.IJsRealm, RuleDeclarationStore, Broiler.HtmlBridge.Jseal.JsValue)"/>)
/// is the same for every rule kind; what differs is whether an edit reaches the sheet.
/// </summary>
/// <remarks>
/// Values arrive already validated for the property by the caller
/// (<see cref="DomBridgeUtils.IsAcceptableInlineValue"/>) and carrying no <c>!important</c> of their own, with
/// any priority attached as the <c>" !important"</c> suffix the map carries (<see cref="CssPriority.Apply"/>).
/// A store may still refuse a value — <see cref="Set"/> answers <see langword="false"/> — when it cannot be
/// written where the store writes it.
/// </remarks>
internal abstract class RuleDeclarationStore
{
    /// <summary>The declared properties, keyed case-insensitively, each value carrying its priority suffix.</summary>
    public abstract IReadOnlyDictionary<string, string> Declared { get; }

    /// <summary>Sets <paramref name="property"/> to <paramref name="value"/>; <see langword="false"/> when refused.</summary>
    public abstract bool Set(string property, string value);

    /// <summary>
    /// Removes <paramref name="property"/> as spelled, and no other spelling of it. Answers nothing a caller
    /// needs: CSSOM's return value is read from <see cref="Declared"/> first.
    /// </summary>
    public abstract void Remove(string property);

    /// <summary>Replaces every declaration with those <paramref name="cssText"/> parses into.</summary>
    public abstract void ReplaceWith(string cssText);
}

/// <summary>
/// A declaration block that lives only on its declaration object: a plain property map, so an edit is seen
/// by that object and nothing else.
/// </summary>
/// <remarks>
/// What every rule's <c>style</c> was before style rules were written through to their sheet
/// (<see cref="ModelRuleDeclarationStore"/>), and what the declaration blocks that are not a style rule's
/// still are: <c>@font-face</c> and <c>@page</c> descriptors, <c>@position-try</c>, and a keyframe's
/// declarations. Descriptors are not properties, so the property validator every write goes through is the
/// wrong one for them, and nothing a page computes depends on them; a keyframe's block feeds animation
/// sampling, which has caches of its own and no invalidation route from here.
/// </remarks>
internal sealed class MapRuleDeclarationStore(Dictionary<string, string> map) : RuleDeclarationStore
{
    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Declared => map;

    /// <inheritdoc />
    public override bool Set(string property, string value)
    {
        map[property] = value;
        return true;
    }

    /// <inheritdoc />
    public override void Remove(string property) => map.Remove(property);

    /// <inheritdoc />
    public override void ReplaceWith(string cssText)
    {
        map.Clear();
        foreach (var kv in DomBridgeUtils.ParseStyle(cssText))
            map[kv.Key] = kv.Value;
    }
}
