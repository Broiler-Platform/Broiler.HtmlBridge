using Broiler.CSS;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// A style rule's declaration block as its sheet holds it: reads answer the rule's current declarations,
/// and a write replaces the rule in the sheet's rule list (<see cref="StyleSheetRuleModel.Commit"/>) — the
/// list the <c>getComputedStyle</c> engine and the renderer read — so <c>rule.style.display = 'none'</c>
/// hides what the rule matches, as CSSOM §6.4.3 says a rule's <c>style</c> does.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads</b> build the same property map the detached declaration was built from —
/// <see cref="DomBridgeUtils.ParseStyle"/> over the serialized block — so a page reads the same validation,
/// vendor-prefix aliases and <c>!important</c> suffixes it always did. The map is rebuilt only when the
/// rule's block changed, by any declaration of the rule.
/// </para>
/// <para>
/// <b>Writes</b> apply the one change to the parsed declaration list (<see cref="RuleDeclarationEdits"/>)
/// rather than writing the read map back. That map is lossy — it drops declarations its validator rejects,
/// adds unprefixed aliases, collapses fallbacks and loses order — so writing it back would change
/// declarations the page never touched.
/// </para>
/// <para>
/// <b>Nothing is committed that is not an edit.</b> A write of the value already there, or a removal of a
/// property the rule does not declare, leaves the rule, the sheet's mutated flag and computed style alone.
/// That matters beyond saving work: the first commit switches the sheet the renderer is handed from the
/// author's text to the model serialized (<c>DomBridge.GetStyleElementCssText</c>), and a page that only
/// ever wrote what was already there should keep rendering its own text.
/// </para>
/// <para>
/// <b>A value must be one declaration.</b> Before writes reached the sheet a value was never parsed again;
/// now the serialized sheet is parsed by the style engine and by the renderer, and a custom property accepts
/// almost any value. <c>setProperty('--x', 'a } #probe { display: none')</c> would otherwise close the rule
/// and add one. <see cref="ParseOneDeclaration"/> therefore requires the value to survive a serialize and
/// re-parse as exactly one declaration followed by an untouched sentinel rule, which rejects a top-level
/// <c>;</c>, an unmatched brace and an unclosed string — none of which CSSOM accepts as a value either.
/// </para>
/// </remarks>
internal sealed class ModelRuleDeclarationStore(StyleSheetRuleModel model, StyleSheetRuleModel.RuleCell cell)
    : RuleDeclarationStore
{
    /// <summary>A selector list to serialize a probe rule with; the model cannot build one itself.</summary>
    private static readonly CssSelectorList ProbeSelectors =
        ((CssStyleRule)new CssParser().ParseStyleSheet("a {}").Rules[0]).Selectors;

    private CssDeclarationBlock? _viewSource;
    private Dictionary<string, string> _view = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override IReadOnlyDictionary<string, string> Declared
    {
        get
        {
            var block = cell.Current.Declarations;
            if (!ReferenceEquals(block, _viewSource))
            {
                _view = DomBridgeUtils.ParseStyle(CssSerializer.Serialize(block));
                _viewSource = block;
            }

            return _view;
        }
    }

    /// <inheritdoc />
    /// <remarks>Refused when the value is not exactly one declaration; see the class remarks.</remarks>
    public override bool Set(string property, string value)
    {
        if (ParseOneDeclaration(RuleDeclarationEdits.CssomPropertyName(property), value) is not { } declaration)
            return false;

        Commit(RuleDeclarationEdits.Set(cell.Current.Declarations.Declarations, declaration));
        return true;
    }

    /// <inheritdoc />
    public override void Remove(string property) =>
        Commit(RuleDeclarationEdits.Remove(
            cell.Current.Declarations.Declarations, RuleDeclarationEdits.CssomPropertyName(property), ParseOneDeclaration));

    /// <inheritdoc />
    /// <remarks>
    /// Keeps each parsed declaration its property accepts and that is safe to serialize, in order and without
    /// collapsing repeats, so a <c>display: -webkit-box; display: flex</c> fallback reaches the engine intact.
    /// </remarks>
    public override void ReplaceWith(string cssText) =>
        Commit([.. new CssParser().ParseDeclarations(cssText).Declarations.Where(declaration =>
            DomBridgeUtils.IsAcceptableInlineValue(declaration.Name, declaration.Value.Text) &&
            RoundTrips(declaration))]);

    private void Commit(List<CssDeclaration> edited)
    {
        if (!Same(cell.Current.Declarations.Declarations, edited))
            model.Commit(cell, new CssDeclarationBlock(edited));
    }

    /// <summary>
    /// <paramref name="value"/> (with any <c>!important</c> suffix) parsed as the one declaration of
    /// <paramref name="name"/>, or <see langword="null"/> when it is not exactly that.
    /// </summary>
    private static CssDeclaration? ParseOneDeclaration(string name, string value)
    {
        // A trailing ';' parses as one declaration and an empty one, which the parser drops silently.
        if (CssSyntax.SplitTopLevel(value, ';').Skip(1).Any())
            return null;

        var parsed = new CssParser().ParseDeclarations($"{name}: {value}").Declarations;
        return parsed is [var declaration] &&
               string.Equals(declaration.Name, name, StringComparison.Ordinal) &&
               RoundTrips(declaration)
            ? declaration
            : null;
    }

    /// <summary>
    /// Whether <paramref name="declaration"/>, serialized into a rule and followed by a sentinel rule, parses
    /// back as itself with the sentinel intact.
    /// </summary>
    private static bool RoundTrips(CssDeclaration declaration)
    {
        var text = CssSerializer.Serialize(new CssStyleRule(ProbeSelectors, new CssDeclarationBlock([declaration]), default)) +
                   "\nb { c: d; }";
        var reparsed = new CssParser().ParseStyleSheet(text);
        return reparsed.Diagnostics.All(diagnostic => diagnostic.Severity != CssDiagnosticSeverity.Error) &&
               reparsed.Rules is
               [
                   CssStyleRule { Declarations.Declarations: [var only] },
                   CssStyleRule { Declarations.Declarations: [{ Name: "c" } sentinel] },
               ] &&
               sentinel.Value.Text == "d" &&
               only.Name == declaration.Name &&
               only.Value.Text == declaration.Value.Text &&
               only.Important == declaration.Important;
    }

    private static bool Same(IReadOnlyList<CssDeclaration> current, List<CssDeclaration> edited)
    {
        if (current.Count != edited.Count)
            return false;

        for (var i = 0; i < current.Count; i++)
        {
            if (!string.Equals(current[i].Name, edited[i].Name, StringComparison.Ordinal) ||
                !string.Equals(current[i].Value.Text, edited[i].Value.Text, StringComparison.Ordinal) ||
                current[i].Important != edited[i].Important)
                return false;
        }

        return true;
    }
}
