using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the host resource loader (<see cref="ResourceLoader"/>) that need no network: the
/// local-base-path setting, the file side of the text dispatch, the local sub-resource read and
/// the binary-content policy. The HTTP side is the network suites' (<c>ProfileNetworkTests</c>,
/// <c>StyleSheetFetchOnceTests</c> and their loopback servers).
/// </summary>
public class ResourceLoaderTests
{
    /// <summary>A linked stylesheet's request from the document at <paramref name="documentUrl"/>.</summary>
    private static StyleSheetRequest StyleSheetFrom(string documentUrl) =>
        new(RequestContext.Subresource(DocumentRequestContext.CreateTopLevel(new Uri(documentUrl)), RequestDestination.Style),
            QuirksMode: false);

    private static string FileDocument() => new Uri(Path.Combine(Path.GetTempPath(), "page.html")).AbsoluteUri;

    [Fact]
    public void TheLocalBasePathDefaultsToNullAndRoundTrips()
    {
        using var loader = new ResourceLoader();
        Assert.Null(loader.LocalBasePath);

        loader.LocalBasePath = "/tmp/site";

        Assert.Equal("/tmp/site", loader.LocalBasePath);
    }

    [Fact]
    public void AFileDocumentsStyleSheetIsReadFromDisk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"broiler-loadtext-{Guid.NewGuid():N}.css");
        File.WriteAllText(path, "body { color: green; }");
        try
        {
            using var loader = new ResourceLoader();

            Assert.Equal("body { color: green; }", loader.LoadText(new Uri(path).AbsoluteUri, StyleSheetFrom(FileDocument())));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AWebDocumentsFileStyleSheetIsNotRead()
    {
        // Only a file: document reads local files: an http(s) page naming a file: or UNC sheet must
        // not have it read (on Windows that would open an SMB session for a UNC path).
        var path = Path.Combine(Path.GetTempPath(), $"broiler-loadtext-{Guid.NewGuid():N}.css");
        File.WriteAllText(path, "body { color: red; }");
        try
        {
            using var loader = new ResourceLoader();

            Assert.Null(loader.LoadText(new Uri(path).AbsoluteUri, StyleSheetFrom("https://example.test/page.html")));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void AMissingFileARelativeUrlAndAnUnsupportedSchemeLoadNothing()
    {
        using var loader = new ResourceLoader();
        var request = StyleSheetFrom(FileDocument());

        Assert.Null(loader.LoadText(new Uri(Path.Combine(Path.GetTempPath(), "broiler-nope-xyz.css")).AbsoluteUri, request));
        Assert.Null(loader.LoadText("styles.css", request));          // not absolute
        Assert.Null(loader.LoadText("ftp://example.com/x", request)); // unsupported scheme
    }

    [Fact]
    public void ALocalResourceIsReadAsTextUnlessItsTypeIsBinary()
    {
        using var loader = new ResourceLoader();
        var textPath = Path.Combine(Path.GetTempPath(), $"broiler-local-{Guid.NewGuid():N}.html");
        File.WriteAllText(textPath, "<p>hi</p>");
        try
        {
            // A text type: the content and the type.
            Assert.Equal(("<p>hi</p>", "text/html"), loader.LoadLocalResource(textPath, "text/html"));

            // A binary type: the bytes are not decoded to text, but the type is kept.
            Assert.Equal(((string?)null, "image/png"), loader.LoadLocalResource(textPath, "image/png"));
        }
        finally
        {
            File.Delete(textPath);
        }

        // A missing file: an empty document, not a fetch failure.
        var missing = Path.Combine(Path.GetTempPath(), $"broiler-missing-{Guid.NewGuid():N}.html");
        Assert.Equal(((string?)null, ""), loader.LoadLocalResource(missing, "text/html"));
    }

    [Theory]
    [InlineData("image/png", true)]
    [InlineData("font/woff2", true)]
    [InlineData("audio/mpeg", true)]
    [InlineData("video/mp4", true)]
    [InlineData("application/pdf", true)]
    [InlineData("text/html", false)]
    [InlineData("application/xhtml+xml", false)]
    [InlineData("application/octet-stream", false)]
    [InlineData("", false)]
    public void BinaryContentTypesAreClassified(string mime, bool expected) =>
        Assert.Equal(expected, ResourceLoader.IsBinaryMime(mime));
}
