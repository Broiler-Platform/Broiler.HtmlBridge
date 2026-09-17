using Broiler.CSS;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// CSSOM's <c>setProperty</c> and <c>removeProperty</c> as edits of a parsed declaration list, for the
/// declaration blocks that are written through to a sheet (<see cref="ModelRuleDeclarationStore"/>). Pure
/// functions: each answers the edited list and leaves the decision to commit it to the caller.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is more than "replace the declaration with that name".</b> Chromium stores a declaration
/// block as longhands, so writing <c>margin-top</c> after <c>margin</c> updates one longhand and removing
/// <c>margin</c> removes four. The consumed model keeps what the author wrote — <c>margin: 7px</c> stays one
/// shorthand declaration — and the style engine expands it at cascade time, in declaration order
/// (<c>CssStyleEngine.AddShorthandLonghandSlots</c>). So an edit has to place and remove declarations such
/// that the cascade over the edited list answers what Chromium's longhands would.
/// </para>
/// <para>
/// <b>Which longhands a shorthand covers</b> is the engine's answer, not a table kept here: the keys
/// <see cref="DomBridgeUtils.ExpandCssShorthands"/> adds for the declaration in isolation, which is exactly
/// what the engine's cascade seeds. That answer depends on the value (the expander parses it), so a
/// shorthand's coverage for removal is also asked with <c>initial</c>, the CSS-wide keyword that CSS Cascade
/// §7.3 says sets every longhand; the expanders that do not recognise the keyword (<c>font</c>,
/// <c>outline</c>) cover only what their declared values expand to. The families the engine does not
/// model (<c>flex</c>, <c>grid-*</c>, <c>transition</c> …) cover nothing, so for them an edit is a plain
/// edit of that one name — the engine could not apply more than that either.
/// </para>
/// <para>
/// <b>Except a flow-relative name</b> (<see cref="IsFlowRelative"/>): the engine expands
/// <c>margin-inline</c> into <c>margin-left</c> and <c>margin-right</c>, which is what those resolve to in a
/// horizontal left-to-right box and not what they are. Its longhands are <c>margin-inline-start</c> and
/// <c>-end</c>, and no physical side is one of them in any writing mode, so a physical edit never splits or
/// removes a logical declaration and a logical edit never removes a physical one. Taking the expansion as the
/// longhand set did both, and turned the author's logical declaration into a physical one that is wrong under
/// <c>direction: rtl</c>.
/// </para>
/// </remarks>
internal static class RuleDeclarationEdits
{
    /// <summary>
    /// <paramref name="declarations"/> with <paramref name="written"/> set: every declaration of the same
    /// property, and every longhand (or narrower shorthand) the written value covers, is removed, and the
    /// written declaration takes the place of the first of them — unless a declaration after that place
    /// could override part of it, in which case it is appended so that it wins, as setting it must.
    /// </summary>
    public static List<CssDeclaration> Set(IReadOnlyList<CssDeclaration> declarations, CssDeclaration written)
    {
        var name = written.Name;
        var covered = CoveredBy(name, [written.Value.Text]);
        var edited = new List<CssDeclaration>(declarations.Count + 1);
        var at = -1;
        foreach (var declaration in declarations)
        {
            if (SameProperty(declaration.Name, name) || covered.Contains(declaration.Name) ||
                (covered.Count > 0 && LeavesOf(declaration) is { Count: > 0 } leaves && leaves.Keys.All(covered.Contains)))
            {
                if (at < 0)
                    at = edited.Count;
                continue;
            }

            edited.Add(declaration);
        }

        var writtenKeys = KeysOf(name, covered);
        if (at >= 0 && !edited.Skip(at).Any(later => Overlaps(later, name, writtenKeys)))
            edited.Insert(at, written);
        else
            edited.Add(written);

        return edited;
    }

    /// <summary>
    /// <paramref name="declarations"/> with <paramref name="name"/> removed: its declarations, the
    /// vendor-prefixed ones the declaration's view reads as it, and every longhand it covers. A remaining
    /// shorthand that covers part of what is removed is split into its other longhands when that loses
    /// nothing (see <see cref="Split"/>), and otherwise left as it is.
    /// </summary>
    /// <param name="parse">Parses one <c>name: value</c> declaration, or answers <see langword="null"/>.</param>
    public static List<CssDeclaration> Remove(
        IReadOnlyList<CssDeclaration> declarations, string name, Func<string, string, CssDeclaration?> parse)
    {
        var custom = IsCustom(name);
        var covered = CoveredBy(name, declarations.Where(d => SameProperty(d.Name, name)).Select(d => d.Value.Text));
        var edited = new List<CssDeclaration>(declarations.Count);
        foreach (var declaration in declarations)
        {
            if (SameProperty(declaration.Name, name))
                continue;

            if (!custom && !IsCustom(declaration.Name))
            {
                // The declaration's view aliases -webkit-transform as transform (DomBridgeUtils.ParseStyle),
                // so removing the name the page reads removes the declaration it read it from.
                if (Removes(CssPropertyNames.StripVendorPrefix(declaration.Name)) || covered.Contains(declaration.Name))
                    continue;

                if (LeavesOf(declaration) is { Count: > 0 } leaves && leaves.Keys.Any(Removes))
                {
                    if (leaves.Keys.All(Removes))
                        continue;

                    if (Split(declaration, leaves, Removes, parse) is { } pieces)
                    {
                        edited.AddRange(pieces);
                        continue;
                    }
                }
            }

            edited.Add(declaration);
        }

        return edited;

        bool Removes(string property) =>
            string.Equals(property, name, StringComparison.OrdinalIgnoreCase) || covered.Contains(property);
    }

    /// <summary>
    /// The longhands of <paramref name="shorthand"/> other than those <paramref name="removes"/> names, each
    /// as its own declaration with the shorthand's importance — or <see langword="null"/> when the split would
    /// change what the rule says.
    /// </summary>
    /// <remarks>
    /// A split is only taken when it is <em>complete</em> — the expansion names every longhand
    /// <c>initial</c> does — and <em>verbatim</em> — every longhand value is made of words the author wrote.
    /// Box shorthands (<c>margin: 1px 2px</c>) always are. A component shorthand that omits a component is
    /// not: the expander fills the gap with a value (<c>border: 1px solid</c> gets a fixed color where the
    /// author's rule meant <c>currentcolor</c>), and writing that value into the sheet would change what the
    /// renderer paints. Declining leaves the shorthand in place, so the removed longhand keeps the
    /// shorthand's value — the same answer the page got before rule writes reached the sheet at all.
    /// </remarks>
    private static List<CssDeclaration>? Split(
        CssDeclaration shorthand,
        Dictionary<string, string> leaves,
        Func<string, bool> removes,
        Func<string, string, CssDeclaration?> parse)
    {
        var every = LeavesOf(shorthand.Name, "initial");
        if (every.Count != leaves.Count || !every.Keys.All(leaves.ContainsKey))
            return null;

        var written = WordsOf(shorthand.Value.Text);
        if (leaves.Values.SelectMany(WordsOf).Any(word => !written.Contains(word)))
            return null;

        var pieces = new List<CssDeclaration>(leaves.Count);
        foreach (var (longhand, value) in leaves)
        {
            if (removes(longhand))
                continue;
            if (parse(longhand, shorthand.Important ? value + " !important" : value) is not { } piece)
                return null;
            pieces.Add(piece);
        }

        return pieces;
    }

    /// <summary>
    /// Whether <paramref name="later"/>, a declaration after the place a written one would take, could set a
    /// longhand the written one sets. Deliberately generous — a false answer that should have been true lets
    /// the later declaration override the write, while a true answer that should have been false only moves
    /// the write to the end of the block — so besides the engine's expansions it treats a name family
    /// (<c>border</c> and <c>border-top-width</c>) as overlapping, which also covers a shorthand whose value
    /// the expander cannot parse (<c>margin: var(--m)</c>).
    /// </summary>
    private static bool Overlaps(CssDeclaration later, string name, HashSet<string> writtenKeys)
    {
        if (IsCustom(later.Name) || IsCustom(name))
            return false;
        if (later.Name.Equals("all", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("all", StringComparison.OrdinalIgnoreCase))
            return true;

        var laterKeys = KeysOf(later.Name, Expand(later.Name, later.Value.Text).Keys);
        return writtenKeys.Any(written => laterKeys.Any(key => Related(written, key)));

        static bool Related(string a, string b) =>
            a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + "-", StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + "-", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every property <paramref name="name"/> sets with any of <paramref name="values"/> or with
    /// <c>initial</c> — longhands and the narrower shorthands the expansion passes through.
    /// </summary>
    private static HashSet<string> CoveredBy(string name, IEnumerable<string> values)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (IsCustom(name) || IsFlowRelative(name))
            return covered;

        foreach (var value in values.Append("initial"))
            covered.UnionWith(Expand(name, value).Keys);

        return covered;
    }

    /// <summary><paramref name="name"/>, its unprefixed alias, and <paramref name="expansion"/>.</summary>
    private static HashSet<string> KeysOf(string name, IEnumerable<string> expansion)
    {
        var keys = new HashSet<string>(expansion, StringComparer.OrdinalIgnoreCase) { name };
        keys.Add(CssPropertyNames.StripVendorPrefix(name));
        return keys;
    }

    /// <summary>
    /// The longhands <paramref name="declaration"/> sets, with their values; empty for a longhand, and for a
    /// flow-relative declaration, whose expansion is not its longhands (see the class remarks).
    /// </summary>
    private static Dictionary<string, string> LeavesOf(CssDeclaration declaration) =>
        IsCustom(declaration.Name) || IsFlowRelative(declaration.Name) ? [] : LeavesOf(declaration.Name, declaration.Value.Text);

    private static Dictionary<string, string> LeavesOf(string name, string value)
    {
        var expanded = Expand(name, value);
        foreach (var key in expanded.Keys.ToList())
        {
            // `border` passes through border-width/-style/-color on its way to the twelve side longhands;
            // only the longhands are leaves.
            if (Expand(key, expanded[key]).Count > 0)
                expanded.Remove(key);
        }

        return expanded;
    }

    /// <summary>What the engine's expander adds for <paramref name="name"/> declared alone with <paramref name="value"/>.</summary>
    private static Dictionary<string, string> Expand(string name, string value)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [name] = value };
        DomBridgeUtils.ExpandCssShorthands(map);
        map.Remove(name);
        return map;
    }

    private static HashSet<string> WordsOf(string value) =>
        new(value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);

    internal static bool IsCustom(string name) => name.StartsWith("--", StringComparison.Ordinal);

    /// <summary>
    /// The name CSSOM looks a property up by (<c>setProperty</c>, <c>removeProperty</c>): a custom property's
    /// as written, every other one ASCII-lowercased.
    /// </summary>
    /// <remarks>
    /// ASCII, not <see cref="string.ToLowerInvariant()"/>: a Unicode lowercase folds the Kelvin sign (U+212A)
    /// to <c>k</c>, so <c>"bacKground-color"</c> would address <c>background-color</c>, where CSSOM leaves it
    /// a name no property has.
    /// </remarks>
    internal static string CssomPropertyName(string property) =>
        IsCustom(property)
            ? property
            : string.Create(property.Length, property, static (lowered, source) =>
            {
                for (var i = 0; i < source.Length; i++)
                    lowered[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + ('a' - 'A')) : source[i];
            });

    /// <summary>
    /// Whether <paramref name="name"/> is a flow-relative property — one with an <c>inline</c> or <c>block</c>
    /// segment (<c>margin-inline</c>, <c>border-block-end-color</c>, <c>inset-inline-start</c> …).
    /// </summary>
    /// <remarks>
    /// By name rather than by list, so a flow-relative property the engine learns to expand later is covered
    /// without a change here. It also matches sizes such as <c>inline-size</c>, which are longhands and have no
    /// expansion to exclude, so the wider match costs nothing.
    /// </remarks>
    private static bool IsFlowRelative(string name) =>
        !IsCustom(name) &&
        name.Split('-').Any(segment =>
            segment.Equals("inline", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("block", StringComparison.OrdinalIgnoreCase));

    /// <summary>CSSOM matches a custom property's name case-sensitively and every other one ASCII case-insensitively.</summary>
    private static bool SameProperty(string a, string b) =>
        IsCustom(a) || IsCustom(b)
            ? string.Equals(a, b, StringComparison.Ordinal)
            : string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
