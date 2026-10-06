using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// One entry of a form's entry list (HTML "constructing the entry list"): a name and a string, or, with
/// <see cref="IsFile"/>, a file -- one the user chose, whose name is <see cref="Value"/>, or the empty file
/// a file input with nothing chosen contributes.
/// </summary>
internal readonly record struct FormEntry(string Name, string Value, bool IsFile = false)
{
    /// <summary>For a file entry, the file the user chose; null for the empty one.</summary>
    public ChosenFile? File { get; init; }

    /// <summary>For a file entry with a <see cref="File"/>, the page's <c>File</c> object for it -- the one <c>input.files</c> lists.</summary>
    public JsValue FileObject { get; init; }
}
