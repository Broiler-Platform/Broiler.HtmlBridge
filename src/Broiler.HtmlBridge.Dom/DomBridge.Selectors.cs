using Broiler.Dom;
using Broiler.CSS.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Compatibility entry points over the shared canonical-DOM selector matcher.
/// </summary>
public sealed partial class DomBridge
{
    // Per-bridge selector matcher (Phase 2 item 4 de-globalization, 2026-07-17): the `:checked` state
    // provider reads the per-bridge FormControl table, so the matcher (and MatchesSelector) is now an
    // instance owned by the bridge — was a process-static shared matcher over the static runtime table.
    // Initialized in the constructor (a field initializer cannot capture `this`).
    private readonly CssSelectorMatcher _selectorMatcher;

    internal bool MatchesSelector(
        DomElement element,
        string selector,
        DomElement? scope = null) =>
        _selectorMatcher.Matches(element, selector, scope);

    private sealed class BridgeSelectorStateProvider(DomBridge bridge) : ICssSelectorStateProvider
    {
        public bool? IsChecked(DomElement element)
        {
            if (element is not DomElement bridgeElement)
                return null;

            return bridge.FormControlStateFor(bridgeElement).Checked.TryGet(out var value)
                ? value is true
                : null;
        }
    }

    internal static string AsciiToLower(string input)
    {
        var characters = input.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (characters[index] is >= 'A' and <= 'Z')
                characters[index] = (char)(characters[index] + 32);
        }
        return new string(characters);
    }
}
