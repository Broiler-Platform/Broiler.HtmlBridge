namespace Broiler.HtmlBridge;

/// <summary>
/// A file the user chose in a host's file picker for a page's <c>&lt;input type="file"&gt;</c>, as the page
/// receives it (<c>InteractiveSession.SetFilesByUser</c>).
/// </summary>
/// <param name="Name">Its name without its folders: <c>File.name</c>, and the input's value after <c>C:\fakepath\</c>.</param>
/// <param name="Type">Its media type, as the host decides it from its name -- <c>text/plain</c> for a <c>.txt</c>; empty when it does not know.</param>
/// <param name="LastModified">When it was last written: <c>File.lastModified</c>.</param>
/// <param name="Content">Its bytes, as the page reads them and a submission sends them.</param>
public sealed record ChosenFile(string Name, string Type, DateTimeOffset LastModified, byte[] Content);
