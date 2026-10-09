using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the <c>Window</c> (HTML §7.1) and <c>Navigator</c> (HTML §8.9) interfaces,
/// verifying constructors, prototypes, WebIDL illegal constructor checks, prototype accessor descriptors,
/// illegal receiver checks, absence of own properties on <c>navigator</c>, and sub-window mirroring.
/// </summary>
public class WindowAndNavigatorInterfaceTests
{
    private const string PageUrl = "https://example.test/interfaces";
    private const string PageHtml = "<!doctype html><html><body><iframe id=\"sub\" srcdoc=\"hello\"></iframe><div id=\"out\"></div></body></html>";

    private static string Run(string expression, bool decode = true) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(expression)], PageHtml, PageUrl), decode);

    [Theory]
    [InlineData("Window")]
    [InlineData("Navigator")]
    public void InterfaceConstructorGlobalsExist(string interfaceName)
    {
        Assert.Equal("function", Run($"typeof {interfaceName}"));
        Assert.Equal("function", Run($"typeof window.{interfaceName}"));
        Assert.Equal(interfaceName, Run($"{interfaceName}.name"));
        Assert.Equal("0", Run($"String({interfaceName}.length)"));
    }

    [Theory]
    [InlineData("Window")]
    [InlineData("Navigator")]
    public void InterfacePrototypesAreObjectsWithConstructor(string interfaceName)
    {
        Assert.Equal("object", Run($"typeof {interfaceName}.prototype"));
        Assert.Equal("true", Run($"{interfaceName}.prototype.constructor === {interfaceName}"));
    }

    [Theory]
    [InlineData("Window", "[object Window]")]
    [InlineData("Navigator", "[object Navigator]")]
    public void InterfacePrototypesHaveCorrectToStringTag(string interfaceName, string expectedTag)
    {
        Assert.Equal(expectedTag, Run($"Object.prototype.toString.call({interfaceName}.prototype)"));
    }

    [Theory]
    [InlineData("new Window()")]
    [InlineData("Window()")]
    [InlineData("new Navigator()")]
    [InlineData("Navigator()")]
    public void DirectConstructionThrowsTypeError(string expression)
    {
        Assert.Equal("threw TypeError: Illegal constructor", Run(expression));
    }

    [Fact]
    public void WindowInheritanceAndIdentity_MatchesWebIdl()
    {
        Assert.Equal("true", Run("Object.getPrototypeOf(Window) === EventTarget"));
        Assert.Equal("true", Run("Object.getPrototypeOf(Window.prototype) === EventTarget.prototype"));
        Assert.Equal("true", Run("window instanceof Window"));
        Assert.Equal("true", Run("window instanceof EventTarget"));
        Assert.Equal("true", Run("Object.getPrototypeOf(window) === Window.prototype"));
        Assert.Equal("[object Window]", Run("Object.prototype.toString.call(window)"));
        Assert.Equal("false", Run("'ontouchstart' in Window"));
    }

    [Fact]
    public void WindowNavigator_IsInstanceOfNavigator()
    {
        Assert.Equal("object", Run("typeof window.navigator"));
        Assert.Equal("true", Run("window.navigator === navigator"));
        Assert.Equal("true", Run("navigator instanceof Navigator"));
        Assert.Equal("true", Run("window.navigator instanceof window.Navigator"));
        Assert.Equal("true", Run("Object.getPrototypeOf(navigator) === Navigator.prototype"));
        Assert.Equal("[object Navigator]", Run("Object.prototype.toString.call(navigator)"));
    }

    [Fact]
    public void WindowNavigator_HasNoOwnProperties()
    {
        Assert.Equal("0", Run("String(Object.getOwnPropertyNames(navigator).length)"));
        Assert.Equal("0", Run("String(Object.keys(navigator).length)"));
        Assert.Equal("undefined", Run("String(Object.getOwnPropertyDescriptor(navigator, 'userAgent'))"));
        Assert.Equal("undefined", Run("String(Object.getOwnPropertyDescriptor(navigator, 'platform'))"));
        Assert.Equal("undefined", Run("String(Object.getOwnPropertyDescriptor(navigator, 'sendBeacon'))"));
        Assert.Equal("undefined", Run("String(Object.getOwnPropertyDescriptor(navigator, 'storage'))"));
        Assert.Equal("undefined", Run("String(Object.getOwnPropertyDescriptor(navigator, 'plugins'))"));
        Assert.Equal("undefined", Run("String(Object.getOwnPropertyDescriptor(navigator, 'userActivation'))"));
    }

    [Theory]
    [InlineData("userAgent")]
    [InlineData("platform")]
    [InlineData("language")]
    [InlineData("languages")]
    [InlineData("cookieEnabled")]
    [InlineData("onLine")]
    [InlineData("vendor")]
    [InlineData("vendorSub")]
    [InlineData("appCodeName")]
    [InlineData("appName")]
    [InlineData("appVersion")]
    [InlineData("product")]
    [InlineData("productSub")]
    [InlineData("webdriver")]
    [InlineData("maxTouchPoints")]
    [InlineData("hardwareConcurrency")]
    [InlineData("deviceMemory")]
    [InlineData("pdfViewerEnabled")]
    [InlineData("plugins")]
    [InlineData("mimeTypes")]
    [InlineData("storage")]
    [InlineData("permissions")]
    [InlineData("userAgentData")]
    [InlineData("webkitTemporaryStorage")]
    [InlineData("webkitPersistentStorage")]
    [InlineData("userActivation")]
    public void NavigatorPrototypeHasAccessorDescriptors(string propertyName)
    {
        const string script =
            "(function(prop) {" +
            "  var d = Object.getOwnPropertyDescriptor(Navigator.prototype, prop);" +
            "  if (!d) return 'missing';" +
            "  return (typeof d.get) + ';' + (typeof d.set) + ';' + d.enumerable + ';' + d.configurable;" +
            "})";

        Assert.Equal("function;undefined;true;true", Run($"{script}('{propertyName}')"));
    }

    [Theory]
    [InlineData("javaEnabled", 0)]
    [InlineData("getGamepads", 0)]
    [InlineData("getBattery", 0)]
    [InlineData("requestMediaKeySystemAccess", 2)]
    [InlineData("sendBeacon", 2)]
    public void NavigatorPrototypeHasMethodDescriptors(string methodName, int expectedArity)
    {
        const string script =
            "(function(method) {" +
            "  var d = Object.getOwnPropertyDescriptor(Navigator.prototype, method);" +
            "  if (!d) return 'missing';" +
            "  return (typeof d.value) + ';' + d.value.length + ';' + d.writable + ';' + d.enumerable + ';' + d.configurable;" +
            "})";

        Assert.Equal($"function;{expectedArity};true;true;true", Run($"{script}('{methodName}')"));
    }

    [Theory]
    [InlineData("userAgent")]
    [InlineData("platform")]
    [InlineData("language")]
    [InlineData("languages")]
    [InlineData("cookieEnabled")]
    [InlineData("onLine")]
    [InlineData("vendor")]
    [InlineData("vendorSub")]
    [InlineData("appCodeName")]
    [InlineData("appName")]
    [InlineData("appVersion")]
    [InlineData("product")]
    [InlineData("productSub")]
    [InlineData("webdriver")]
    [InlineData("maxTouchPoints")]
    [InlineData("hardwareConcurrency")]
    [InlineData("deviceMemory")]
    [InlineData("pdfViewerEnabled")]
    [InlineData("plugins")]
    [InlineData("mimeTypes")]
    [InlineData("storage")]
    [InlineData("permissions")]
    [InlineData("userAgentData")]
    [InlineData("webkitTemporaryStorage")]
    [InlineData("webkitPersistentStorage")]
    [InlineData("userActivation")]
    public void NavigatorPrototypeGettersRefuseIllegalReceivers(string propertyName)
    {
        var runEmptyObject = Run($"Object.getOwnPropertyDescriptor(Navigator.prototype, '{propertyName}').get.call({{}})");
        Assert.Equal($"threw TypeError: Failed to read the '{propertyName}' property from 'Navigator': Illegal invocation", runEmptyObject);

        var runNull = Run($"Object.getOwnPropertyDescriptor(Navigator.prototype, '{propertyName}').get.call(null)");
        Assert.Equal($"threw TypeError: Failed to read the '{propertyName}' property from 'Navigator': Illegal invocation", runNull);

        var runPlainPrototype = Run($"Object.getOwnPropertyDescriptor(Navigator.prototype, '{propertyName}').get.call(Object.create(Navigator.prototype))");
        Assert.Equal($"threw TypeError: Failed to read the '{propertyName}' property from 'Navigator': Illegal invocation", runPlainPrototype);
    }

    [Theory]
    [InlineData("javaEnabled")]
    [InlineData("getGamepads")]
    [InlineData("getBattery")]
    [InlineData("requestMediaKeySystemAccess")]
    [InlineData("sendBeacon")]
    public void NavigatorPrototypeMethodsRefuseIllegalReceivers(string methodName)
    {
        var runEmptyObject = Run($"Navigator.prototype.{methodName}.call({{}})");
        Assert.Equal($"threw TypeError: Failed to execute '{methodName}' on 'Navigator': Illegal invocation", runEmptyObject);

        var runNull = Run($"Navigator.prototype.{methodName}.call(null)");
        Assert.Equal($"threw TypeError: Failed to execute '{methodName}' on 'Navigator': Illegal invocation", runNull);

        var runPlainPrototype = Run($"Navigator.prototype.{methodName}.call(Object.create(Navigator.prototype))");
        Assert.Equal($"threw TypeError: Failed to execute '{methodName}' on 'Navigator': Illegal invocation", runPlainPrototype);
    }

    [Fact]
    public void NavigatorProperties_ReturnExpectedValues()
    {
        Assert.Equal("Mozilla", Run("navigator.appCodeName"));
        Assert.Equal("Netscape", Run("navigator.appName"));
        Assert.Equal("Gecko", Run("navigator.product"));
        Assert.Equal("20030107", Run("navigator.productSub"));
        Assert.Equal("Win32", Run("navigator.platform"));
        Assert.Equal("en-US", Run("navigator.language"));
        Assert.Equal("true", Run("Array.isArray(navigator.languages) && navigator.languages[0] === 'en-US'"));
        Assert.Equal("true", Run("String(navigator.onLine)"));
        Assert.Equal("true", Run("String(navigator.webdriver)"));
        Assert.Equal("0", Run("String(navigator.maxTouchPoints)"));
        Assert.Equal("false", Run("String(navigator.pdfViewerEnabled)"));
        Assert.Equal("false", Run("String(navigator.javaEnabled())"));
        Assert.Equal("true", Run("Array.isArray(navigator.getGamepads()) && navigator.getGamepads().length === 0"));
        Assert.Equal("0", Run("String(navigator.plugins.length)"));
        Assert.Equal("0", Run("String(navigator.mimeTypes.length)"));
        Assert.Equal("true", Run("navigator.plugins === navigator.plugins"));
        Assert.Equal("true", Run("navigator.mimeTypes === navigator.mimeTypes"));
    }

    [Fact]
    public void SubWindow_MirrorsWindowAndNavigatorConstructors()
    {
        const string script =
            "(function() {" +
            "  var iframe = document.getElementById('sub');" +
            "  var subWin = iframe.contentWindow;" +
            "  return (" +
            "    (typeof subWin.Window === 'function') + ';' +" +
            "    (typeof subWin.Navigator === 'function') + ';' +" +
            "    (subWin.Window === Window) + ';' +" +
            "    (subWin.Navigator === Navigator) + ';' +" +
            "    (subWin instanceof Window) + ';' +" +
            "    (subWin.navigator instanceof Navigator)" +
            "  );" +
            "})()";

        Assert.Equal("true;true;true;true;true;true", Run(script));
    }
}
