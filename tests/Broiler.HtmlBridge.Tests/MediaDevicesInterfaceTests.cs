using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for <c>MediaDevices</c> and <c>MediaDeviceInfo</c> interfaces (W3C Media Capture and Streams),
/// verifying constructors, prototype chain inheriting from <c>EventTarget</c>, receiver brand checks,
/// <c>navigator.mediaDevices</c> accessor, enumerateDevices, getUserMedia, getDisplayMedia,
/// getSupportedConstraints, legacy <c>navigator.getUserMedia</c>, and sub-window mirroring.
/// </summary>
public class MediaDevicesInterfaceTests
{
    private const string PageUrl = "https://example.test/mediadevices";
    private const string PageHtml = "<!doctype html><html><body><iframe id=\"sub\" srcdoc=\"hello\"></iframe><div id=\"out\"></div></body></html>";

    private static string Run(string expression, bool decode = true) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(expression)], PageHtml, PageUrl), decode);

    private static string RunScript(string script, bool decode = true) =>
        PageProbe.OutOf(PageProbe.Render([script], PageHtml, PageUrl), decode);

    [Theory]
    [InlineData("MediaDevices")]
    [InlineData("MediaDeviceInfo")]
    public void InterfaceConstructorGlobalsExist(string interfaceName)
    {
        Assert.Equal("function", Run($"typeof {interfaceName}"));
        Assert.Equal("function", Run($"typeof window.{interfaceName}"));
        Assert.Equal(interfaceName, Run($"{interfaceName}.name"));
        Assert.Equal("0", Run($"String({interfaceName}.length)"));
    }

    [Theory]
    [InlineData("new MediaDevices()")]
    [InlineData("MediaDevices()")]
    [InlineData("new MediaDeviceInfo()")]
    [InlineData("MediaDeviceInfo()")]
    public void DirectConstructionThrowsTypeError(string expression)
    {
        var result = Run(expression);
        Assert.StartsWith("threw TypeError: ", result);
        Assert.Contains("Illegal constructor", result);
    }

    [Theory]
    [InlineData("MediaDevices", "[object MediaDevices]")]
    [InlineData("MediaDeviceInfo", "[object MediaDeviceInfo]")]
    public void InterfacePrototypesAreObjectsWithCorrectToStringTag(string interfaceName, string expectedTag)
    {
        Assert.Equal("object", Run($"typeof {interfaceName}.prototype"));
        Assert.Equal("true", Run($"{interfaceName}.prototype.constructor === {interfaceName}"));
        Assert.Equal(expectedTag, Run($"Object.prototype.toString.call({interfaceName}.prototype)"));
    }

    [Fact]
    public void MediaDevicesInheritanceAndIdentity_MatchesWebIdl()
    {
        Assert.Equal("true", Run("Object.getPrototypeOf(MediaDevices) === EventTarget"));
        Assert.Equal("true", Run("Object.getPrototypeOf(MediaDevices.prototype) === EventTarget.prototype"));
        Assert.Equal("true", Run("navigator.mediaDevices instanceof MediaDevices"));
        Assert.Equal("true", Run("navigator.mediaDevices instanceof EventTarget"));
        Assert.Equal("true", Run("Object.getPrototypeOf(navigator.mediaDevices) === MediaDevices.prototype"));
        Assert.Equal("[object MediaDevices]", Run("Object.prototype.toString.call(navigator.mediaDevices)"));
        Assert.Equal("true", Run("navigator.mediaDevices === navigator.mediaDevices"));
    }

    [Theory]
    [InlineData("Reflect.get(Navigator.prototype, 'mediaDevices', {})", "Failed to read the 'mediaDevices' property from 'Navigator': Illegal invocation")]
    [InlineData("Navigator.prototype.getUserMedia.call({})", "Failed to execute 'getUserMedia' on 'Navigator': Illegal invocation")]
    [InlineData("MediaDevices.prototype.enumerateDevices.call({})", "Failed to execute 'enumerateDevices' on 'MediaDevices': Illegal invocation")]
    [InlineData("MediaDevices.prototype.getUserMedia.call({})", "Failed to execute 'getUserMedia' on 'MediaDevices': Illegal invocation")]
    [InlineData("MediaDevices.prototype.getDisplayMedia.call({})", "Failed to execute 'getDisplayMedia' on 'MediaDevices': Illegal invocation")]
    [InlineData("MediaDevices.prototype.getSupportedConstraints.call({})", "Failed to execute 'getSupportedConstraints' on 'MediaDevices': Illegal invocation")]
    [InlineData("Reflect.get(MediaDevices.prototype, 'ondevicechange', {})", "Failed to read the 'ondevicechange' property from 'MediaDevices': Illegal invocation")]
    [InlineData("Reflect.set(MediaDevices.prototype, 'ondevicechange', null, {})", "Failed to set the 'ondevicechange' property on 'MediaDevices': Illegal invocation")]
    [InlineData("Reflect.get(MediaDeviceInfo.prototype, 'deviceId', {})", "Illegal invocation")]
    [InlineData("MediaDeviceInfo.prototype.toJSON.call({})", "Illegal invocation")]
    public void ReceiverBrandChecksThrowTypeError(string expression, string expectedSnippet)
    {
        var result = Run(expression);
        Assert.StartsWith("threw TypeError: ", result);
        Assert.Contains(expectedSnippet, result);
    }

    [Fact]
    public void EnumerateDevices_ResolvesWithEmptyArray()
    {
        const string script = """
            navigator.mediaDevices.enumerateDevices().then(devices => {
                document.getElementById('out').textContent =
                    Array.isArray(devices) + ':' + devices.length;
            }).catch(e => {
                document.getElementById('out').textContent = 'err:' + e.name;
            });
            """;
        Assert.Equal("true:0", RunScript(script));
    }

    [Fact]
    public void GetUserMedia_RejectsWithNotFoundError()
    {
        const string script = """
            navigator.mediaDevices.getUserMedia({ audio: true }).then(() => {
                document.getElementById('out').textContent = 'unexpected-success';
            }).catch(e => {
                document.getElementById('out').textContent = (e instanceof DOMException) + ':' + e.name;
            });
            """;
        Assert.Equal("true:NotFoundError", RunScript(script));
    }

    [Fact]
    public void GetDisplayMedia_RejectsWithNotAllowedError()
    {
        const string script = """
            navigator.mediaDevices.getDisplayMedia().then(() => {
                document.getElementById('out').textContent = 'unexpected-success';
            }).catch(e => {
                document.getElementById('out').textContent = (e instanceof DOMException) + ':' + e.name;
            });
            """;
        Assert.Equal("true:NotAllowedError", RunScript(script));
    }

    [Fact]
    public void GetSupportedConstraints_ReturnsSupportedConstraintsDictionary()
    {
        Assert.Equal("true", Run("typeof navigator.mediaDevices.getSupportedConstraints() === 'object'"));
        Assert.Equal("true", Run("navigator.mediaDevices.getSupportedConstraints().deviceId === true"));
        Assert.Equal("true", Run("navigator.mediaDevices.getSupportedConstraints().width === true"));
        Assert.Equal("true", Run("navigator.mediaDevices.getSupportedConstraints().height === true"));
        Assert.Equal("true", Run("navigator.mediaDevices.getSupportedConstraints().aspectRatio === true"));
        Assert.Equal("true", Run("navigator.mediaDevices.getSupportedConstraints().frameRate === true"));
    }

    [Fact]
    public void LegacyGetUserMedia_BehavesCorrectly()
    {
        Assert.Equal("function", Run("typeof navigator.getUserMedia"));
        Assert.Equal("3", Run("String(navigator.getUserMedia.length)"));
        Assert.Equal("undefined", Run("String(navigator.getUserMedia())"));

        const string script = """
            var callbackFired = false;
            navigator.getUserMedia({ audio: true }, null, function(err) {
                callbackFired = (err instanceof DOMException) && err.name === 'NotFoundError';
            });
            document.getElementById('out').textContent = String(callbackFired);
            """;
        Assert.Equal("true", RunScript(script));
    }

    [Fact]
    public void SubWindow_MirrorsMediaDevicesGlobals()
    {
        Assert.Equal("function", Run("typeof document.getElementById('sub').contentWindow.MediaDevices"));
        Assert.Equal("function", Run("typeof document.getElementById('sub').contentWindow.MediaDeviceInfo"));
        Assert.Equal("true", Run("document.getElementById('sub').contentWindow.MediaDevices === MediaDevices"));
        Assert.Equal("true", Run("document.getElementById('sub').contentWindow.MediaDeviceInfo === MediaDeviceInfo"));
        Assert.Equal("true", Run("document.getElementById('sub').contentWindow.navigator.mediaDevices instanceof MediaDevices"));
    }
}
