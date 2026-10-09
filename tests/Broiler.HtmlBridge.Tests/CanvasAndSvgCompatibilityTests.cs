namespace Broiler.HtmlBridge.Tests;

public sealed class CanvasAndSvgCompatibilityTests
{
    private static string Run(string script) => PageProbe.OutOf(PageProbe.Render(
        ["var ctx=document.getElementById('c').getContext('2d');" + PageProbe.Probe("(function(){" + script + "})()")],
        "<html><body><canvas id='c' width='40' height='40'></canvas><svg xmlns='http://www.w3.org/2000/svg'><g id='g'><rect id='r' x='2' y='3' width='10' height='12'/><text id='t' x='4' y='20' style='font:20px Arial'>Wi</text></g></svg><div id='out'></div></body></html>",
        "https://example.test/graphics"));

    [Fact]
    public void RadialGradientProducesDifferentCenterAndEdgePixels()
    {
        Assert.Equal("true", Run("var g=ctx.createRadialGradient(20,20,0,20,20,20);g.addColorStop(0,'red');g.addColorStop(1,'blue');ctx.fillStyle=g;ctx.fillRect(0,0,40,40);var p=ctx.getImageData(0,0,40,40).data;return p[(20*40+20)*4]>200 && p[(20*40+39)*4+2]>200;"));
    }

    [Fact]
    public void GradientStateSurvivesSaveAndRestoreButCanvasResizeResetsIt()
    {
        Assert.Equal("true", Run("var g=ctx.createLinearGradient(0,0,40,0);g.addColorStop(0,'red');ctx.fillStyle=g;ctx.save();ctx.fillStyle='blue';ctx.restore();var ok=ctx.fillStyle===g;ctx.canvas.width=40;return ok && ctx.fillStyle==='#000000';"));
    }

    [Theory]
    [InlineData("ctx.createRadialGradient(0,0,-1,0,0,10)", "IndexSizeError")]
    [InlineData("ctx.createRadialGradient(0,0,1,Infinity,0,10)", "TypeError")]
    [InlineData("ctx.createLinearGradient(0,0,1,1).addColorStop(2,'red')", "IndexSizeError")]
    [InlineData("ctx.createLinearGradient(0,0,1,1).addColorStop(.5,'invalid-color')", "SyntaxError")]
    [InlineData("ctx.ellipse(0,0,-1,2,0,0,1)", "IndexSizeError")]
    public void InvalidGraphicsArgumentsReportTheRequiredError(string expression, string expected)
        => Assert.Equal(expected, Run("try{" + expression + ";return 'no error';}catch(e){return e.name;}"));

    [Fact]
    public void EllipseAndCurvesWritePixels()
    {
        Assert.Equal("true", Run("ctx.fillStyle='red';ctx.beginPath();ctx.ellipse(20,20,12,5,0,0,Math.PI*2);ctx.fill();var p=ctx.getImageData(20,20,1,1).data;var ok=p[0]===255&&p[3]===255;ctx.clearRect(0,0,40,40);ctx.beginPath();ctx.moveTo(2,30);ctx.bezierCurveTo(2,2,38,2,38,30);ctx.quadraticCurveTo(20,10,2,30);ctx.stroke();var d=ctx.getImageData(0,0,40,40).data;var n=0;for(var i=3;i<d.length;i+=4)if(d[i])n++;return ok&&n>20;"));
    }

    [Fact]
    public void SvgBoundsAreInUserSpaceAndTextUsesFontAdvances()
    {
        Assert.Equal("true", Run("var r=document.getElementById('r');r.setAttribute('transform','translate(100 100)');var b=r.getBBox();var t=document.getElementById('t');var w=t.getSubStringLength(0,1),i=t.getSubStringLength(1,1),e=t.getExtentOfChar(1);return b.x===2&&b.y===3&&b.width===10&&b.height===12&&w>i&&Math.abs(e.x-(4+w))<.1&&e.height>0&&document.getElementById('g').getBBox().width>0;"));
    }

    [Fact]
    public void TransformSnapshotsAreIndependentAndStateResets()
        => Assert.Equal("true", Run("ctx.translate(10,8);ctx.scale(2,3);var m=ctx.getTransform();ctx.save();ctx.resetTransform();ctx.restore();m.e=99;var n=ctx.getTransform();ctx.fillStyle='red';ctx.fillRect(0,0,3,3);var p=ctx.getImageData(12,10,1,1).data;ctx.canvas.width=40;return n.a===2&&n.d===3&&n.e===10&&n.f===8&&p[0]===255&&ctx.getTransform().isIdentity;"));

    [Fact]
    public void TransformAffectsCanvasImageCopiesAndClear()
        => Assert.Equal("true", Run("ctx.fillStyle='red';ctx.fillRect(0,0,4,4);ctx.translate(10,10);ctx.drawImage(ctx.canvas,0,0);var a=ctx.getImageData(11,11,1,1).data;ctx.clearRect(0,0,4,4);var b=ctx.getImageData(11,11,1,1).data;return a[0]===255&&a[3]===255&&b[3]===0&&ctx.getImageData(1,1,1,1).data[3]===255;"));

    [Fact]
    public void GradientTextUsesGradientColor()
        => Assert.Equal("true", Run("var g=ctx.createLinearGradient(0,0,40,0);g.addColorStop(0,'blue');g.addColorStop(1,'blue');ctx.fillStyle=g;ctx.font='20px Arial';ctx.fillText('W',2,25);var d=ctx.getImageData(0,0,40,40).data,n=0;for(var i=0;i<d.length;i+=4)if(d[i+3]&&d[i+2]>200&&d[i]===0)n++;return n>20;"));

    [Fact]
    public void RenderProjectionWithoutAHostCodecPreservesTheScriptVisibleDom()
    {
        var html = PageProbe.Render(["var c=document.getElementById('c'),x=c.getContext('2d');x.fillStyle='red';x.fillRect(0,0,4,4);" +
            PageProbe.Probe("c.hasAttribute('data-broiler-canvas-bitmap')")],
            "<html><body><canvas id='c' width='4' height='4'></canvas><div id='out'></div></body></html>",
            "https://example.test/canvas-projection");
        Assert.Equal("false", PageProbe.OutOf(html));
        Assert.DoesNotContain("data-broiler-canvas-bitmap=", html);
    }

    [Fact]
    public void SvgCharacterIndexErrorsAreDomExceptions()
        => Assert.Equal("IndexSizeError", Run("try{document.getElementById('t').getExtentOfChar(2);}catch(e){return e.name;}"));

    [Fact]
    public void SvgMeasurementsRespondToCssFontChanges()
        => Assert.Equal("true", Run("var t=document.getElementById('t'),a=t.getComputedTextLength();t.style.fontSize='40px';var b=t.getComputedTextLength();return b>a*1.8;"));
}
