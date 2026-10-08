using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the <c>Screen</c> interface (CSSOM View §4) and <c>ScreenOrientation</c> (Screen Orientation API §4),
/// verifying constructors, prototypes, branding, prototype accessors, receiver checks, and live viewport reflection.
/// </summary>
public class ScreenInterfaceTests
{
    private const string PageUrl = "https://example.test/screen";
    private const string PageHtml = "<!doctype html><html><body><div id=\"out\"></div></body></html>";

    private static string Run(string expression, bool decode = true) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(expression)], PageHtml, PageUrl), decode);

    [Theory]
    [InlineData("Screen")]
    [InlineData("ScreenOrientation")]
    public void InterfaceConstructorGlobalsExist(string interfaceName)
    {
        Assert.Equal("function", Run($"typeof {interfaceName}"));
        Assert.Equal("function", Run($"typeof window.{interfaceName}"));
        Assert.Equal(interfaceName, Run($"{interfaceName}.name"));
        Assert.Equal("0", Run($"String({interfaceName}.length)"));
    }

    [Theory]
    [InlineData("Screen")]
    [InlineData("ScreenOrientation")]
    public void InterfacePrototypesAreObjectsWithConstructor(string interfaceName)
    {
        Assert.Equal("object", Run($"typeof {interfaceName}.prototype"));
        Assert.Equal("true", Run($"{interfaceName}.prototype.constructor === {interfaceName}"));
    }

    [Theory]
    [InlineData("Screen", "[object Screen]")]
    [InlineData("ScreenOrientation", "[object ScreenOrientation]")]
    public void InterfacePrototypesHaveCorrectToStringTag(string interfaceName, string expectedTag)
    {
        Assert.Equal(expectedTag, Run($"Object.prototype.toString.call({interfaceName}.prototype)"));
    }

    [Theory]
    [InlineData("new Screen()")]
    [InlineData("Screen()")]
    [InlineData("new ScreenOrientation()")]
    [InlineData("ScreenOrientation()")]
    public void DirectConstructionThrowsTypeError(string expression)
    {
        Assert.Equal("threw TypeError: Illegal constructor", Run(expression));
    }

    [Fact]
    public void WindowScreen_IsInstanceOfScreen()
    {
        Assert.Equal("object", Run("typeof window.screen"));
        Assert.Equal("true", Run("window.screen === screen"));
        Assert.Equal("true", Run("screen instanceof Screen"));
        Assert.Equal("true", Run("window.screen instanceof window.Screen"));
        Assert.Equal("true", Run("Object.getPrototypeOf(screen) === Screen.prototype"));
        Assert.Equal("[object Screen]", Run("Object.prototype.toString.call(screen)"));
    }

    [Fact]
    public void WindowScreen_HasNoOwnProperties()
    {
        Assert.Equal("0", Run("String(Object.getOwnPropertyNames(screen).length)"));
        Assert.Equal("0", Run("String(Object.keys(screen).length)"));
        Assert.Equal("undefined", Run("String(Object.getOwnPropertyDescriptor(screen, 'width'))"));
        Assert.Equal("undefined", Run("String(Object.getOwnPropertyDescriptor(screen, 'height'))"));
        Assert.Equal("undefined", Run("String(Object.getOwnPropertyDescriptor(screen, 'availWidth'))"));
    }

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("availWidth")]
    [InlineData("availHeight")]
    [InlineData("availLeft")]
    [InlineData("availTop")]
    [InlineData("colorDepth")]
    [InlineData("pixelDepth")]
    [InlineData("orientation")]
    public void ScreenPrototypeHasAccessorDescriptors(string propertyName)
    {
        const string script =
            "(function(prop) {" +
            "  var d = Object.getOwnPropertyDescriptor(Screen.prototype, prop);" +
            "  if (!d) return 'missing';" +
            "  return (typeof d.get) + ';' + (typeof d.set) + ';' + d.enumerable + ';' + d.configurable;" +
            "})";

        Assert.Equal("function;undefined;true;true", Run($"{script}('{propertyName}')"));
    }

    [Theory]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("availWidth")]
    [InlineData("availHeight")]
    [InlineData("availLeft")]
    [InlineData("availTop")]
    [InlineData("colorDepth")]
    [InlineData("pixelDepth")]
    [InlineData("orientation")]
    public void ScreenPrototypeGettersRefuseIllegalReceivers(string propertyName)
    {
        var runEmptyObject = Run($"Object.getOwnPropertyDescriptor(Screen.prototype, '{propertyName}').get.call({{}})");
        Assert.Equal($"threw TypeError: Failed to read the '{propertyName}' property from 'Screen': Illegal invocation", runEmptyObject);

        var runNull = Run($"Object.getOwnPropertyDescriptor(Screen.prototype, '{propertyName}').get.call(null)");
        Assert.Equal($"threw TypeError: Failed to read the '{propertyName}' property from 'Screen': Illegal invocation", runNull);

        var runPlainPrototype = Run($"Object.getOwnPropertyDescriptor(Screen.prototype, '{propertyName}').get.call(Object.create(Screen.prototype))");
        Assert.Equal($"threw TypeError: Failed to read the '{propertyName}' property from 'Screen': Illegal invocation", runPlainPrototype);
    }

    [Fact]
    public void ScreenProperties_ReturnExpectedValues()
    {
        // In the headless test engine, default viewport is 1024x768
        Assert.Equal("1024", Run("String(screen.width)"));
        Assert.Equal("768", Run("String(screen.height)"));
        Assert.Equal("1024", Run("String(screen.availWidth)"));
        Assert.Equal("768", Run("String(screen.availHeight)"));
        Assert.Equal("0", Run("String(screen.availLeft)"));
        Assert.Equal("0", Run("String(screen.availTop)"));
        Assert.Equal("24", Run("String(screen.colorDepth)"));
        Assert.Equal("24", Run("String(screen.pixelDepth)"));
    }

    [Fact]
    public void ScreenOrientation_MeetsSpecification()
    {
        Assert.Equal("true", Run("screen.orientation === screen.orientation"));
        Assert.Equal("true", Run("screen.orientation instanceof ScreenOrientation"));
        Assert.Equal("true", Run("Object.getPrototypeOf(screen.orientation) === ScreenOrientation.prototype"));
        Assert.Equal("[object ScreenOrientation]", Run("Object.prototype.toString.call(screen.orientation)"));
        Assert.Equal("landscape-primary", Run("screen.orientation.type"));
        Assert.Equal("0", Run("String(screen.orientation.angle)"));
        Assert.Equal("undefined", Run("typeof screen.orientation.unlock()"));
    }

    [Fact]
    public void SannysoftProbe_ScreenPrototypeWidthDescriptorSucceeds()
    {
        // Line 212 of fpCollect.min.js:
        // Object.getOwnPropertyDescriptor(Object.getPrototypeOf(screen), "width").get.toString()
        const string script =
            "(function() {" +
            "  var proto = Object.getPrototypeOf(screen);" +
            "  var d = Object.getOwnPropertyDescriptor(proto, 'width');" +
            "  var type = typeof d;" +
            "  var fnStr = d.get.toString();" +
            "  return type + ';' + (typeof fnStr);" +
            "})()";

        Assert.Equal("object;string", Run(script));
    }
}
