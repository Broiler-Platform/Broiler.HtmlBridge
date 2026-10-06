using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The files a user chose for a page's file input in the host's picker: what <c>input.files</c> lists,
/// what <c>input.value</c> reads, what a <c>FormData</c> and a submission carry -- and the <c>input</c> and
/// <c>change</c> the choice fires, or the <c>cancel</c> of a picker closed without one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The files stayed the host's.</b> A host that shows its own picker kept what the user chose for its
/// own submissions, read from the disk as it sent them, and the page knew nothing: <c>files</c> stayed an
/// empty list, <c>value</c> empty, no <c>change</c> fired, a <c>formdata</c> listener saw an empty file and
/// a script could not read or upload the file.
/// </para>
/// <para>
/// <b>As HTML and Chromium have it</b> (a picker cannot be driven from a
/// script, so its events are HTML's "update the file selection" and Chromium's
/// <c>FileInputType::SetFilesAndDispatchEvents</c>): the choice fires <c>input</c> -- bubbling, composed --
/// then <c>change</c>, and a closed picker <c>cancel</c>. <c>files</c> is one <c>FileList</c> per input,
/// <c>value</c> is <c>C:\fakepath\</c> and the first file's name, setting it to the empty string clears
/// the choice and setting it to anything else throws.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // The files the user chose for each file input, each with the page's File object for it, which every
    // read of input.files and every FormData built from the form hands out.
    private readonly ConditionalWeakTable<DomElement, List<(ChosenFile File, JsValue Object)>> _chosenFiles = new();

    /// <summary>The files the user chose for <paramref name="input"/>, with the page's object for each; none for an input nobody chose for.</summary>
    internal IReadOnlyList<(ChosenFile File, JsValue Object)> ChosenFilesOf(DomElement input) =>
        _chosenFiles.TryGetValue(input, out var files) ? files : [];

    /// <summary>A file input's <c>value</c>: <c>C:\fakepath\</c> and its first file's name, or the empty string (measured).</summary>
    internal string FileInputValue(DomElement input) =>
        ChosenFilesOf(input) is [var first, ..] ? @"C:\fakepath\" + first.File.Name : string.Empty;

    /// <summary>
    /// <c>value = value</c> on a file input: the empty string clears what the user chose, with no events;
    /// anything else is Chromium's <c>InvalidStateError</c> (measured).
    /// </summary>
    internal void SetFileInputValue(DomElement input, string value, IJsRealm realm)
    {
        if (value.Length > 0)
        {
            throw realm.DomError("InvalidStateError",
                "Failed to set the 'value' property on 'HTMLInputElement': This input element accepts a filename, which may only be programmatically set to the empty string.");
        }

        if (_chosenFiles.Remove(input))
            NoteElementStateChange();
    }

    /// <summary>
    /// The user chose <paramref name="files"/> for the page's file input <paramref name="fileInputIndex"/>,
    /// counted in tree order among the page's file inputs: they replace what it held, and it hears
    /// <c>input</c> and <c>change</c>. Answers whether the choice changed anything; the files it holds
    /// already, a disabled input or one that is not there change nothing and fire nothing.
    /// </summary>
    internal bool SetFilesByUser(int fileInputIndex, IReadOnlyList<ChosenFile> files)
    {
        if (_realm is null || FileInputAt(fileInputIndex) is not { } input || IsDisabledFormControl(input))
            return false;

        var held = ChosenFilesOf(input);
        if (held.Count == files.Count && held.Select(static entry => entry.File).SequenceEqual(files, ChosenFileComparer.Instance))
            return false;

        var realm = Realm;
        _chosenFiles.AddOrUpdate(input, files.Select(file =>
            (file, _blobs.CreateFile(realm, file.Content, file.Name, file.Type, file.LastModified.ToUnixTimeMilliseconds()))).ToList());

        NoteElementStateChange();
        FireChangeNotification(input, "input", composed: true);
        if (input.IsConnected)
        {
            MarkUserInteracted(input);
            FireChangeNotification(input, "change", composed: false);
        }

        return true;
    }

    /// <summary>The user closed the host's picker for the page's file input <paramref name="fileInputIndex"/> without choosing: it hears <c>cancel</c>, which bubbles.</summary>
    internal bool CancelFilePickByUser(int fileInputIndex)
    {
        if (_realm is null || FileInputAt(fileInputIndex) is not { } input || IsDisabledFormControl(input))
            return false;

        var realm = Realm;
        DispatchKeyboardEvent(input, NewTrustedEvent(realm, "cancel", bubbles: true, cancelable: false, composed: false, InterfacePrototype(realm, "Event")));
        return true;
    }

    /// <summary>The page's file input <paramref name="index"/>, counted in tree order, or null.</summary>
    private DomElement? FileInputAt(int index) =>
        index < 0
            ? null
            : _document.Descendants().OfType<DomElement>()
                .Where(static element => element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) &&
                                         TryGetAttribute(element, "type", out var type) &&
                                         type.Trim().Equals("file", StringComparison.OrdinalIgnoreCase))
                .ElementAtOrDefault(index);

    /// <summary>Two choices are the same when each file has the same name, type, time and bytes.</summary>
    private sealed class ChosenFileComparer : IEqualityComparer<ChosenFile>
    {
        public static readonly ChosenFileComparer Instance = new();

        public bool Equals(ChosenFile? x, ChosenFile? y) =>
            ReferenceEquals(x, y) ||
            x is not null && y is not null && x.Name == y.Name && x.Type == y.Type && x.LastModified == y.LastModified &&
            x.Content.AsSpan().SequenceEqual(y.Content);

        public int GetHashCode(ChosenFile obj) => HashCode.Combine(obj.Name, obj.Content.Length);
    }
}
