namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// One entry of a form's entry list (HTML "constructing the entry list"): a name and a string, or, with
/// <see cref="IsFile"/>, the empty file a file input with nothing chosen contributes.
/// </summary>
internal readonly record struct FormEntry(string Name, string Value, bool IsFile = false);
