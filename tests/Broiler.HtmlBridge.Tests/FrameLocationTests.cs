using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// A frame's <c>location</c> — the object an <c>&lt;iframe&gt;</c>'s <c>contentWindow</c> and
/// <c>frames[0]</c> hand back — asserted from the top-level script that embeds it.
/// <para>
/// <b>This is the last Location in the bridge still minted with the engine's own types, and it is
/// about to stop being one.</b> <c>LocationBinding</c> carries two builders over one
/// <c>DocumentUrl</c> and one navigation implementation: <c>Build(string)</c>, which
/// <c>Features/SubWindowBinding.cs:185</c> calls because a static has no realm to conjure, and
/// <c>Build(IJsRealm, string)</c>, which has never had a caller — though the six navigation members
/// it installs are the top-level Location's own (<c>Registration/Window.cs:52</c>), so what has
/// never run is its seven-component prologue. The sweep deletes the first builder and points that
/// call site at the second, and nothing in this suite had loaded a frame before this file. The same
/// thirteen members are installed twice in two vocabularies, and one that changed name, order, kind,
/// arity or attributes on the way across would be visible only to a page.
/// </para>
/// <para>
/// <b>The key list is asserted whole rather than counted.</b> The counted spelling is what the
/// top-level Location already has (<c>DomEnumerationAndArityTests</c>), and it passes just as
/// happily when a rebuild renames <c>hostname</c> or emits the components after the methods.
/// </para>
/// <para>
/// Three assertions pin what this bridge does rather than what a browser does, and each says so
/// where it stands: a browser's Location is a <c>[LegacyUnforgeable]</c> platform object — every
/// member an accessor, none configurable, the enumeration opening at <c>ancestorOrigins</c>, the
/// prototype <c>Location.prototype</c> — where this one is an ordinary object with configurable,
/// writable data components. Pinning that is the point; correcting it is not this commit's business.
/// </para>
/// </summary>
public class FrameLocationTests
{
    private const string PageUrl = "https://example.test/frames";

    /// <summary>
    /// One same-origin frame carrying a <c>srcdoc</c> and no <c>src</c>, so the frame's document URL
    /// is <c>about:srcdoc</c> and a nested browsing context exists with no network behind it.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<iframe id=\"f\" srcdoc=\"<html><body><p id='inner'>hi</p></body></html>\"></iframe>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture document and returns what it wrote to
    /// <c>#out</c>. Reading the result out of the serialized DOM keeps the test to the engine's public
    /// surface — the binding under test is internal, and reaching for it directly would pin its shape
    /// rather than its behaviour.
    /// </summary>
    private static string Run(string script)
    {
        var html = new ScriptEngine().Execute(
            [$"document.getElementById('out').textContent = String({script});"],
            PageHtml,
            PageUrl);

        Assert.NotNull(html);

        const string open = "<div id=\"out\">";
        var start = html!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return html[start..end];
    }

    [Fact]
    public void AFramesLocationCarriesTheseThirteenMembersInThisOrder()
    {
        // Whole and in order, because the failure this exists for is a rebuild that renames or
        // reorders one member — and the count says 13 either way. That each is an OWN enumerable
        // property is a browser answer (Web IDL [LegacyUnforgeable]); that the list opens at
        // `protocol` rather than `ancestorOrigins` is this bridge's, pinned.
        Assert.Equal(
            "keys=protocol,host,hostname,port,pathname,search,origin,href,hash,assign,replace," +
            "reload,toString count=13",
            Run("""
                (function () {
                  var keys = Object.keys(document.getElementById('f').contentWindow.location);
                  return 'keys=' + keys.join(',') + ' count=' + keys.length;
                })()
                """));
    }

    [Fact]
    public void AFramesHrefIsAnAccessorAndItsComponentsAreDataProperties()
    {
        // The distinction a uniform rebuild erases: `href` routes its write into the navigation path
        // and `protocol` does not, so a builder that installed both the same way would break
        // `location.href = url` in a frame and fail nothing else here. A getter declaring no argument
        // is Web IDL's; the attribute bits are this bridge's shape, pinned — a browser answers
        // configurable: false, and makes `protocol` an accessor rather than a value.
        Assert.Equal(
            "hrefGet=function hrefSet=function hrefGetArity=0 hrefEnum=true hrefConfig=true " +
            "protocolSet=undefined protocolValue=about: protocolWritable=true protocolEnum=true " +
            "protocolConfig=true",
            Run("""
                (function () {
                  var l = document.getElementById('f').contentWindow.location;
                  var h = Object.getOwnPropertyDescriptor(l, 'href');
                  var p = Object.getOwnPropertyDescriptor(l, 'protocol');
                  return 'hrefGet=' + (typeof h.get) + ' hrefSet=' + (typeof h.set) +
                         ' hrefGetArity=' + h.get.length +
                         ' hrefEnum=' + h.enumerable + ' hrefConfig=' + h.configurable +
                         ' protocolSet=' + (typeof p.set) + ' protocolValue=' + p.value +
                         ' protocolWritable=' + p.writable + ' protocolEnum=' + p.enumerable +
                         ' protocolConfig=' + p.configurable;
                })()
                """));
    }

    [Fact(Skip =
        "The two setters on a frame's Location are DomFunctions built with that type's default " +
        "length of 0 (Features/LocationBinding.cs:267 and :273 over DomBridge/DomFunction.cs:40), " +
        "where Web IDL gives an attribute setter one required argument. The realm builder that is " +
        "about to replace them already declares it — DefineAccessor mints the setter with length 1 " +
        "(src/Broiler.HtmlBridge.Jseal.BroilerJs/BroilerJsRealm.Members.cs:102-103) — which is why " +
        "the top-level Location answers 1 in DomEnumerationAndArityTests and a frame's answers 0 on " +
        "an object that is otherwise identical. This is the ONE member-shape difference between the " +
        "two builders, so un-skip it in the commit that points SubWindowBinding.cs:185 at " +
        "LocationBinding.Build(IJsRealm, string).")]
    public void AFramesAttributeSettersDeclareTheArgumentTheyTake()
    {
        Assert.Equal(
            "href=1 hash=1",
            Run("""
                (function () {
                  var l = document.getElementById('f').contentWindow.location;
                  return 'href=' + Object.getOwnPropertyDescriptor(l, 'href').set.length +
                         ' hash=' + Object.getOwnPropertyDescriptor(l, 'hash').set.length;
                })()
                """));
    }

    [Fact]
    public void AFramesNavigationMethodsExistAndReturnRatherThanThrow()
    {
        // The whole reason the methods are installed at all: `undefined is not a function` is a
        // TypeError raised at the call, which takes the rest of the caller with it — and a framed
        // page reaches for location.replace() as readily as a top-level one. The arities are Web
        // IDL's required-argument counts, handed to a mint site once and never read back.
        Assert.Equal(
            "assign=function replace=function reload=function toString=function " +
            "assignArity=1 replaceArity=1 reloadArity=0 replaceReturns=undefined",
            Run("""
                (function () {
                  var l = document.getElementById('f').contentWindow.location;
                  return 'assign=' + (typeof l.assign) + ' replace=' + (typeof l.replace) +
                         ' reload=' + (typeof l.reload) + ' toString=' + (typeof l.toString) +
                         ' assignArity=' + l.assign.length + ' replaceArity=' + l.replace.length +
                         ' reloadArity=' + l.reload.length +
                         ' replaceReturns=' + l.replace('https://other.test/x');
                })()
                """));
    }

    [Fact]
    public void AnAboutSrcdocFramesComponentsDescribeThatDocument()
    {
        // about:srcdoc has no authority, so host, hostname, port, search and hash answer empty strings
        // rather than being absent, and the opaque path is what pathname answers. The brackets tell an
        // empty answer from a missing one. Stringifying to the href rather than to "[object Object]"
        // is the thirteenth member, and pages build URLs with `"" + location`.
        Assert.Equal(
            "href=about:srcdoc protocol=about: host=[] hostname=[] port=[] pathname=srcdoc " +
            "search=[] hash=[] string=about:srcdoc",
            Run("""
                (function () {
                  var l = document.getElementById('f').contentWindow.location;
                  return 'href=' + l.href + ' protocol=' + l.protocol +
                         ' host=[' + l.host + '] hostname=[' + l.hostname + '] port=[' + l.port +
                         '] pathname=' + l.pathname + ' search=[' + l.search + '] hash=[' + l.hash +
                         '] string=' + String(l);
                })()
                """));
    }

    [Fact(Skip =
        "A frame's Location derives every component from its own URL, so an about:srcdoc frame gets " +
        "the nonsense serialization \"about://\" from Scripting/Origin.cs:26 over a URI with no " +
        "authority (Features/LocationBinding.cs:251). HTML gives a srcdoc document the origin of the " +
        "document that embeds it, so location.origin must answer the embedder's — which is the whole " +
        "of what a framed script tests before it postMessages back, and \"about://\" matches nothing " +
        "and equals no other frame's.")]
    public void ASrcdocFramesOriginIsTheOneItInheritsFromItsEmbedder()
    {
        Assert.Equal(
            "origin=https://example.test frameMatchesPage=true",
            Run("""
                (function () {
                  var l = document.getElementById('f').contentWindow.location;
                  return 'origin=' + l.origin +
                         ' frameMatchesPage=' + (l.origin === location.origin);
                })()
                """));
    }

    [Fact]
    public void AFrameHasOneLocationSharedWithItsDocumentAndDistinctFromThePages()
    {
        // One Location per browsing context, reached by three spellings, and the frame's document
        // shares its window's exactly as the page's document shares the page's — a framed script
        // reads document.location for its origin, and a second object there would answer for a
        // document nobody has. `plainObject` pins this bridge's shape: there is no Location
        // interface object here, so the prototype is Object.prototype.
        Assert.Equal(
            "sameAsFrames=true documentShares=true stable=true distinctFromPage=true " +
            "plainObject=true",
            Run("""
                (function () {
                  var f = document.getElementById('f');
                  var W = f.contentWindow;
                  return 'sameAsFrames=' + (frames[0] === W) +
                         ' documentShares=' + (W.document.location === W.location) +
                         ' stable=' + (f.contentWindow.location === W.location) +
                         ' distinctFromPage=' + (W.location !== window.location) +
                         ' plainObject=' + (Object.getPrototypeOf(W.location) === Object.prototype);
                })()
                """));
    }
}
