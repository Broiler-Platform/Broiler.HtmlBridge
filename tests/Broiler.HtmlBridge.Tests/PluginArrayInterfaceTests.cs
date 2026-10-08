using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the <c>PluginArray</c>, <c>MimeTypeArray</c>, <c>Plugin</c>, and <c>MimeType</c>
/// interfaces (HTML §8.9.1.5), verifying constructors, prototypes, branding, empty collections,
/// and collection semantics on <c>navigator.plugins</c> and <c>navigator.mimeTypes</c>.
/// </summary>
public class PluginArrayInterfaceTests
{
    private const string PageUrl = "https://example.test/plugins";
    private const string PageHtml = "<!doctype html><html><body><div id=\"out\"></div></body></html>";

    private static string Run(string expression) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(expression)], PageHtml, PageUrl));

    [Theory]
    [InlineData("PluginArray")]
    [InlineData("MimeTypeArray")]
    [InlineData("Plugin")]
    [InlineData("MimeType")]
    public void InterfaceConstructorGlobalsExist(string interfaceName)
    {
        Assert.Equal("function", Run($"typeof {interfaceName}"));
        Assert.Equal("function", Run($"typeof window.{interfaceName}"));
        Assert.Equal(interfaceName, Run($"{interfaceName}.name"));
        Assert.Equal("0", Run($"String({interfaceName}.length)"));
    }

    [Theory]
    [InlineData("PluginArray")]
    [InlineData("MimeTypeArray")]
    [InlineData("Plugin")]
    [InlineData("MimeType")]
    public void InterfacePrototypesAreObjectsWithConstructor(string interfaceName)
    {
        Assert.Equal("object", Run($"typeof {interfaceName}.prototype"));
        Assert.Equal("true", Run($"{interfaceName}.prototype.constructor === {interfaceName}"));
    }

    [Theory]
    [InlineData("PluginArray", "[object PluginArray]")]
    [InlineData("MimeTypeArray", "[object MimeTypeArray]")]
    [InlineData("Plugin", "[object Plugin]")]
    [InlineData("MimeType", "[object MimeType]")]
    public void InterfacePrototypesHaveCorrectToStringTag(string interfaceName, string expectedTag)
    {
        Assert.Equal(expectedTag, Run($"Object.prototype.toString.call({interfaceName}.prototype)"));
    }

    [Theory]
    [InlineData("new PluginArray()")]
    [InlineData("PluginArray()")]
    [InlineData("new MimeTypeArray()")]
    [InlineData("MimeTypeArray()")]
    [InlineData("new Plugin()")]
    [InlineData("Plugin()")]
    [InlineData("new MimeType()")]
    [InlineData("MimeType()")]
    public void DirectConstructionThrowsTypeError(string expression)
    {
        Assert.Equal("threw TypeError: Illegal constructor", Run(expression));
    }

    [Fact]
    public void NavigatorPlugins_IsInstanceOfPluginArray()
    {
        Assert.Equal("true", Run("navigator.plugins instanceof PluginArray"));
        Assert.Equal("true", Run("window.navigator.plugins instanceof window.PluginArray"));
        Assert.Equal("[object PluginArray]", Run("Object.prototype.toString.call(navigator.plugins)"));
    }

    [Fact]
    public void NavigatorMimeTypes_IsInstanceOfMimeTypeArray()
    {
        Assert.Equal("true", Run("navigator.mimeTypes instanceof MimeTypeArray"));
        Assert.Equal("true", Run("window.navigator.mimeTypes instanceof window.MimeTypeArray"));
        Assert.Equal("[object MimeTypeArray]", Run("Object.prototype.toString.call(navigator.mimeTypes)"));
    }

    [Fact]
    public void NavigatorPlugins_EmptyCollectionSemantics()
    {
        Assert.Equal("0", Run("navigator.plugins.length"));
        Assert.Equal("undefined", Run("typeof navigator.plugins[0]"));
        Assert.Equal("null", Run("String(navigator.plugins.item(0))"));
        Assert.Equal("null", Run("String(navigator.plugins.item(1))"));
        Assert.Equal("null", Run("String(navigator.plugins.namedItem('PDF Viewer'))"));
        Assert.Equal("undefined", Run("typeof navigator.plugins.refresh()"));
        Assert.Equal("0", Run("String(Array.from(navigator.plugins).length)"));
        Assert.Equal("0", Run("String(Object.keys(navigator.plugins).length)"));
        Assert.Equal("0", Run("String([...navigator.plugins].length)"));
    }

    [Fact]
    public void NavigatorMimeTypes_EmptyCollectionSemantics()
    {
        Assert.Equal("0", Run("navigator.mimeTypes.length"));
        Assert.Equal("undefined", Run("typeof navigator.mimeTypes[0]"));
        Assert.Equal("null", Run("String(navigator.mimeTypes.item(0))"));
        Assert.Equal("null", Run("String(navigator.mimeTypes.item(1))"));
        Assert.Equal("null", Run("String(navigator.mimeTypes.namedItem('application/pdf'))"));
        Assert.Equal("0", Run("String(Array.from(navigator.mimeTypes).length)"));
        Assert.Equal("0", Run("String(Object.keys(navigator.mimeTypes).length)"));
        Assert.Equal("0", Run("String([...navigator.mimeTypes].length)"));
    }

    [Fact]
    public void PrototypeOperationsAreFunctionsWithExpectedArity()
    {
        Assert.Equal("function", Run("typeof PluginArray.prototype.item"));
        Assert.Equal("1", Run("String(PluginArray.prototype.item.length)"));
        Assert.Equal("function", Run("typeof PluginArray.prototype.namedItem"));
        Assert.Equal("1", Run("String(PluginArray.prototype.namedItem.length)"));
        Assert.Equal("function", Run("typeof PluginArray.prototype.refresh"));
        Assert.Equal("0", Run("String(PluginArray.prototype.refresh.length)"));

        Assert.Equal("function", Run("typeof MimeTypeArray.prototype.item"));
        Assert.Equal("1", Run("String(MimeTypeArray.prototype.item.length)"));
        Assert.Equal("function", Run("typeof MimeTypeArray.prototype.namedItem"));
        Assert.Equal("1", Run("String(MimeTypeArray.prototype.namedItem.length)"));

        Assert.Equal("function", Run("typeof Plugin.prototype.item"));
        Assert.Equal("1", Run("String(Plugin.prototype.item.length)"));
        Assert.Equal("function", Run("typeof Plugin.prototype.namedItem"));
        Assert.Equal("1", Run("String(Plugin.prototype.namedItem.length)"));
    }

    [Fact]
    public void SannysoftDetectionScriptLine154_ExecutesWithoutThrowing()
    {
        // Replicates line 154 of Sannysoft's bot detection script:
        // if (!( navigator.plugins instanceof PluginArray ) || navigator.plugins.length === 0 || window.navigator.plugins[0].toString() !== '[object Plugin]')
        const string script =
            "(function() {" +
            "  var isPluginArray = navigator.plugins instanceof PluginArray;" +
            "  var len = navigator.plugins.length;" +
            "  var failed = !(navigator.plugins instanceof PluginArray) || navigator.plugins.length === 0 || window.navigator.plugins[0].toString() !== '[object Plugin]';" +
            "  return isPluginArray + ';' + len + ';' + failed;" +
            "})()";

        Assert.Equal("true;0;true", Run(script));
    }
}
