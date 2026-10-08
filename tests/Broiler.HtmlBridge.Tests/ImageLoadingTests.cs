using Broiler.Graphics.Imaging;
using Broiler.HtmlBridge;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

public class ImageLoadingTests
{
    private const string Page = "<!doctype html><body><div id=out></div></body>";
    private const string Url = "https://example.test/images";
    private const string RedGreen = "data:image/test;base64,AQ==";
    private const string Blue = "data:image/test,%02";

    // The image decoder is deliberately session-local. Other tests rely on there being no global
    // codec catalog. These pixels distinguish a real copy from a stub returning transparent black.
    private static BBitmap Decode(byte[] bytes) => bytes.Length == 1 ? bytes[0] switch
    {
        1 => new BBitmap(2, 1, [255, 0, 0, 255, 0, 255, 0, 255]),
        2 => new BBitmap(1, 1, [0, 0, 255, 255]),
        _ => throw new InvalidDataException("Bad image"),
    } : throw new InvalidDataException("Bad image");

    private static string Run(string script, string? html = null, DomBridgeSessionOptions? options = null, string url = Url)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(options ?? new() { ImageDecoder = Decode }));
        var result = engine.Execute([script], html ?? Page, url);
        Assert.NotNull(result);
        return PageProbe.OutOf(result!, decode: true);
    }

    [Fact]
    public void ConstructorCreatesARealDetachedElementWithSharedPrototype() =>
        Assert.Equal("true|true|true|IMG|true|7|9|true|0", Run("""
            var i = new Image(7, 9);
            document.getElementById('out').textContent = [i instanceof Image, i instanceof HTMLImageElement,
                i instanceof HTMLElement, i.tagName, Image.prototype === HTMLImageElement.prototype,
                i.width, i.height, i.complete, i.naturalWidth].join('|');
            """));

    [Fact]
    public void DetachedDataImageLoadsAsATaskAndDrawsActualPixels() =>
        Assert.Equal("assigned|listener:true|true:2:1:2:1|255,0,0,255,0,255,0,255", Run($$"""
            var out = document.getElementById('out'), events = [];
            var i = new Image();
            i.addEventListener('load', function(e) { events.push('listener:' + (e.target === i)); });
            i.src = '{{RedGreen}}';
            i.onload = function() {
                events.push([this === i && i.complete, i.naturalWidth, i.naturalHeight, i.width, i.height].join(':'));
                var c = document.createElement('canvas'); c.width = 2; c.height = 1;
                var ctx = c.getContext('2d'); ctx.drawImage(i, 0, 0);
                events.push(Array.from(ctx.getImageData(0,0,2,1).data).join(','));
                out.textContent = events.join('|');
            };
            events.push(i.complete ? 'synchronous' : 'assigned');
            """));

    [Theory]
    [InlineData("data:image/png;base64,invalid!")]
    [InlineData("data:image/test,%ff")]
    [InlineData("")]
    public void InvalidImagesErrorAndFinish(string source) => Assert.Equal("error|true|0|InvalidStateError", Run($$"""
        var i = new Image();
        i.onerror = function() {
            var error = '';
            try { document.createElement('canvas').getContext('2d').drawImage(i, 0, 0); }
            catch(e) { error = e.name; }
            document.getElementById('out').textContent = ['error',i.complete,i.naturalWidth,error].join('|');
        };
        i.src = '{{source}}';
        """));

    [Fact]
    public void SourceReplacementCancelsEarlierQueuedLoadsAndErrors() =>
        Assert.Equal("load:1", Run($$"""
            var i = new Image(), events = [];
            i.onload = function() { events.push('load:' + i.naturalWidth); };
            i.onerror = function() { events.push('error'); };
            i.src = 'data:image/png;base64,bad!'; i.src = '{{RedGreen}}'; i.src = '{{Blue}}';
            setTimeout(function() { document.getElementById('out').textContent = events.join('|'); }, 10);
            """));

    [Fact]
    public void RemovingSourceCancelsPendingRequest() => Assert.Equal("0|true|0", Run($$"""
        var i = new Image(), count = 0;
        i.onload = i.onerror = function(){count++;};
        i.src = '{{RedGreen}}'; i.removeAttribute('src');
        setTimeout(function(){document.getElementById('out').textContent=[count,i.complete,i.naturalWidth].join('|');},10);
        """));

    [Fact]
    public void CreateElementAndSetAttributeShareTheImageLifecycle() => Assert.Equal("load|2", Run($$"""
        var i = document.createElement('img');
        i.addEventListener('load', function() { document.getElementById('out').textContent='load|' + i.naturalWidth; });
        i.setAttribute('src','{{RedGreen}}'); document.body.appendChild(i);
        """));

    [Fact]
    public void ImagePromiseChainCompletesTheFingerprintStyleWait() => Assert.Equal("0,0,255,255|done", Run($$"""
        Promise.all([new Promise(function(resolve) {
            var i = new Image(), ctx = document.createElement('canvas').getContext('2d');
            i.onload = function(){ctx.drawImage(i,0,0); resolve(Array.from(ctx.getImageData(0,0,1,1).data).join(','));};
            i.onerror = function(){resolve('error');}; i.src='{{Blue}}';
        }), Promise.resolve('done')]).then(function(values){document.getElementById('out').textContent=values.join('|');});
        """));

    [Fact]
    public void CroppingScalingAlphaAndCanvasCopyUseDecodedPixels() => Assert.Equal("0,255,0,128|0,255,0,128", Run($$"""
        var i=new Image(); i.onload=function(){
            var c=document.createElement('canvas'); c.width=2;c.height=1;
            var ctx=c.getContext('2d'); ctx.globalAlpha=0.5; ctx.drawImage(i,1,0,1,1,0,0,2,1);
            var d=document.createElement('canvas'), dest=d.getContext('2d'); dest.drawImage(c,0,0);
            document.getElementById('out').textContent = Array.from(ctx.getImageData(0,0,1,1).data).join(',') + '|' +
                Array.from(dest.getImageData(1,0,1,1).data).join(',');
        }; i.src='{{RedGreen}}';
        """));

    [Fact]
    public void DisposalDropsImageTasksBeforeDecoding()
    {
        var decoded = 0;
        var engine = new ScriptEngine(new DomBridgeFactory(new() { ImageDecoder = bytes => { decoded++; return Decode(bytes); } }));
        using var session = engine.ExecuteInteractive([$"var i=new Image(); i.src='{RedGreen}';"], [], Page, Url);
        Assert.NotNull(session);
        session!.Dispose();
        Assert.Equal(0, decoded);
    }

    [Fact]
    public void WebDocumentCannotReadLocalImage() => Assert.Equal("error", Run("""
        var i=new Image(); i.onerror=function(){document.getElementById('out').textContent='error';};
        i.onload=function(){document.getElementById('out').textContent='load';};
        i.src='file:///C:/Windows/win.ini';
        """));

    [Fact]
    public void HttpImagesUseTheProfileAndAppearInResourceTiming()
    {
        using var server = new LoopbackCookieServer()
            .Map("/login", new Reply(SetCookies: ["sid=image; Path=/"]))
            .Map("/pixel", new Reply(ContentType: "image/test", BodyBytes: [1], SetCookies: ["image=loaded; Path=/"]));
        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions());
        using (var message = new HttpRequestMessage(HttpMethod.Get, server.Url("/login")))
        using (var response = profile.Send(message, RequestContext.TopLevelNavigation(initiator: null)))
            Assert.Equal(200, response.StatusCode);
        Assert.Equal("2|img|true", Run("""
            var i = new Image(); i.onload=function(){
                var entries=performance.getEntriesByType('resource');
                var entry=entries.filter(function(e){return e.name.indexOf('/pixel')>=0;})[0];
                document.getElementById('out').textContent=[i.naturalWidth,entry.initiatorType,entry.duration>=0].join('|');
            }; i.src='/pixel';
            """, options: new() { Network = profile, Cookies = profile, ImageDecoder = Decode }, url: server.Url("/page")));
        Assert.Contains("sid=image", server.Single("/pixel").Header("Cookie"));
        Assert.Contains(profile.Cookies.Snapshot(), cookie => cookie.Name == "image" && cookie.Value == "loaded");
    }

    [Theory]
    [InlineData(false, "SecurityError|SecurityError|SecurityError|0")]
    [InlineData(true, "readable|data:,|readable|0")]
    public void CrossOriginImageReadbackRequiresCors(bool cors, string expected)
    {
        using var server = new LoopbackCookieServer().Map("/pixel", new Reply(ContentType: "image/test", BodyBytes: [1],
            Headers: [("Access-Control-Allow-Origin", "*")]));
        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions());
        Assert.Equal(expected, Run($$"""
            var i=new Image(); {{(cors ? "i.crossOrigin='anonymous';" : "")}}
            i.onload=function(){
                var c=document.createElement('canvas'), ctx=c.getContext('2d'), values=[];
                ctx.drawImage(i,0,0);
                try { ctx.getImageData(0,0,1,1);values.push('readable'); }catch(e){values.push(e.name);}
                try { values.push(c.toDataURL()); }catch(e){values.push(e.name);}
                var d=document.createElement('canvas'), dest=d.getContext('2d');dest.drawImage(c,0,0);
                try { dest.getImageData(0,0,1,1);values.push('readable'); }catch(e){values.push(e.name);}
                c.width=c.width; values.push(ctx.getImageData(0,0,1,1).data[3]);
                document.getElementById('out').textContent=values.join('|');
            }; i.src='{{server.LocalhostUrl("/pixel")}}';
            """, options: new() { Network = profile, ImageDecoder = Decode }, url: server.Url("/page")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HttpFailuresAndCorsRejectionsDispatchError(bool cors)
    {
        using var server = new LoopbackCookieServer().Map("/pixel",
            new Reply(Status: cors ? 200 : 404, ContentType: "image/test", BodyBytes: [1]));
        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions());
        Assert.Equal("error|true|0", Run($$"""
            var i=new Image(); {{(cors ? "i.crossOrigin='anonymous';" : "")}}
            i.onload=function(){document.getElementById('out').textContent='load';};
            i.onerror=function(){document.getElementById('out').textContent=['error',i.complete,i.naturalWidth].join('|');};
            i.src='{{server.LocalhostUrl("/pixel")}}';
            """, options: new() { Network = profile, ImageDecoder = Decode }, url: server.Url("/page")));
    }

    [Fact]
    public void FrameImageHandlerRunsInItsOwningDocument() => Assert.Equal("true|true", Run($$"""
        var f=document.createElement('iframe');document.body.appendChild(f);
        f.srcdoc="<script>var i=new Image();i.onload=function(){parent.document.getElementById('out').textContent=[i.ownerDocument===document,window===self].join('|');};i.src='{{Blue}}';<\/script>";
        """));

    [Fact]
    public void ExplicitCssZeroDoesNotFallBackToIntrinsicSize() => Assert.Equal("0|0|2", Run($$"""
        var i=new Image(); i.style.width='0px'; i.style.height='0px';
        i.onload=function(){document.getElementById('out').textContent=[i.width,i.height,i.naturalWidth].join('|');};
        i.src='{{RedGreen}}';
        """));

    [Fact]
    public void ChangingSourceFromLoadListenerQueuesANewLoad() => Assert.Equal("2|1", Run($$"""
        var i=new Image(), widths=[];
        i.onload=function(){widths.push(i.naturalWidth);if(widths.length===1)i.src='{{Blue}}';
            else document.getElementById('out').textContent=widths.join('|');};
        i.src='{{RedGreen}}';
        """));

    [Fact]
    public void NavigatingAFrameDropsItsPreviousImageEvent() => Assert.Equal("new", Run($$"""
        var f=document.createElement('iframe');document.body.appendChild(f);
        f.srcdoc="<script>var i=new Image();i.onload=function(){parent.document.getElementById('out').textContent='old';};i.src='{{Blue}}';<\/script>";
        f.srcdoc="<script>parent.document.getElementById('out').textContent='new';<\/script>";
        """));
}
