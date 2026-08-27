using System.Globalization;

namespace Broiler.HtmlBridge.Scripting;

/// <summary>
/// The name one of a document's scripts is known by — in a stack trace, in the profiling timeline,
/// and in the error the host logs when it throws.
/// <para>
/// <c>JSContext.Eval</c> takes the location it reports in stack frames, and every host used to pass
/// none, so every frame of every script read <c>vm.js</c>. A page's scripts are then
/// indistinguishable: an exception at <c>vm.js:3,159</c> could be any of a dozen scripts, one of
/// which is a megabyte of minified vendor code. The labels here are what the hosts already used for
/// their profiling entries and their error messages, so naming the evaluation as well makes the
/// three agree rather than introducing a fourth vocabulary.
/// </para>
/// <para>
/// The numbering is per bucket and in document order, matching the order the host executes them in:
/// <c>inline-0</c> is the first non-deferred script the document names, whether its program came
/// from the element's text, a <c>data:</c> URI or a fetched <c>src</c>. That is a document-relative
/// identity rather than a URL — enough to point at one script, which is what a trace could not do
/// at all before.
/// </para>
/// </summary>
public static class ScriptLabel
{
    /// <summary>The label for the <paramref name="index"/>-th non-deferred script, in document order.</summary>
    public static string Inline(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"inline-{index}");

    /// <summary>The label for the <paramref name="index"/>-th deferred script, in document order.</summary>
    public static string Deferred(int index) =>
        string.Create(CultureInfo.InvariantCulture, $"deferred-{index}");

    /// <summary>The label for an engine-driven ES-module root, which is named by its module key.</summary>
    public static string Module(string key) => "module-" + key;
}
