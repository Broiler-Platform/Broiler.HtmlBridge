using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests for the constructible <c>DocumentFragment</c> interface (DOM §4.6),
/// verifying constructor presence, arity, prototype chain, branding, TypeError when called
/// without <c>new</c>, owner document, detached state, subclassing, child insertion,
/// and fragment emptying on append.
/// </summary>
public class DocumentFragmentConstructorTests
{
    private const string PageUrl = "https://example.test/fragment";
    private const string PageHtml = "<!doctype html><html><body><div id=\"host\"></div><div id=\"out\"></div></body></html>";

    private static string Run(string expression) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(expression)], PageHtml, PageUrl));

    [Fact]
    public void DocumentFragmentConstructorGlobalExists()
    {
        Assert.Equal("function", Run("typeof DocumentFragment"));
        Assert.Equal("function", Run("typeof window.DocumentFragment"));
        Assert.Equal("DocumentFragment", Run("DocumentFragment.name"));
        Assert.Equal("0", Run("String(DocumentFragment.length)"));
    }

    [Fact]
    public void DocumentFragmentPrototypeAndInheritance()
    {
        Assert.Equal("object", Run("typeof DocumentFragment.prototype"));
        Assert.Equal("true", Run("DocumentFragment.prototype.constructor === DocumentFragment"));
        Assert.Equal("true", Run("Object.getPrototypeOf(DocumentFragment.prototype) === Node.prototype"));
        Assert.Equal("[object DocumentFragment]", Run("Object.prototype.toString.call(DocumentFragment.prototype)"));
    }

    [Fact]
    public void CallingWithoutNewThrowsTypeError()
    {
        var result = Run("DocumentFragment()");
        Assert.StartsWith("threw TypeError:", result);
    }

    [Fact]
    public void ConstructingWithNewProducesRealDocumentFragment()
    {
        Assert.Equal(
            "isFrag=true isNode=true isET=true ctor=true proto=true nodeType=11 nodeName=#document-fragment " +
            "ownerDoc=true parentNode=null isConnected=false childNodes=0 hasChildNodes=false tag=[object DocumentFragment]",
            Run("""
                (function () {
                  var frag = new DocumentFragment();
                  return 'isFrag=' + (frag instanceof DocumentFragment) +
                         ' isNode=' + (frag instanceof Node) +
                         ' isET=' + (typeof EventTarget === 'function' ? (frag instanceof EventTarget) : true) +
                         ' ctor=' + (frag.constructor === DocumentFragment) +
                         ' proto=' + (Object.getPrototypeOf(frag) === DocumentFragment.prototype) +
                         ' nodeType=' + frag.nodeType +
                         ' nodeName=' + frag.nodeName +
                         ' ownerDoc=' + (frag.ownerDocument === document) +
                         ' parentNode=' + (frag.parentNode === null ? 'null' : frag.parentNode) +
                         ' isConnected=' + frag.isConnected +
                         ' childNodes=' + frag.childNodes.length +
                         ' hasChildNodes=' + frag.hasChildNodes() +
                         ' tag=' + Object.prototype.toString.call(frag);
                })()
                """));
    }

    [Fact]
    public void SubclassingDocumentFragmentWorks()
    {
        Assert.Equal(
            "sub=true base=true node=true type=11 owner=true customProp=custom",
            Run("""
                (function () {
                  class CustomFrag extends DocumentFragment {
                    constructor() {
                      super();
                      this.extra = 'custom';
                    }
                  }
                  var cf = new CustomFrag();
                  return 'sub=' + (cf instanceof CustomFrag) +
                         ' base=' + (cf instanceof DocumentFragment) +
                         ' node=' + (cf instanceof Node) +
                         ' type=' + cf.nodeType +
                         ' owner=' + (cf.ownerDocument === document) +
                         ' customProp=' + cf.extra;
                })()
                """));
    }

    [Fact]
    public void ChildInsertionAndFragmentEmptyingAfterAppend()
    {
        Assert.Equal(
            "before=1 firstChildParent=frag after=0 inDoc=true parent=BODY",
            Run("""
                (function () {
                  var frag = new DocumentFragment();
                  var div = document.createElement('div');
                  div.id = 'inserted-from-frag';
                  frag.appendChild(div);
                  var before = frag.childNodes.length;
                  var firstChildParent = div.parentNode === frag ? 'frag' : 'other';

                  document.body.appendChild(frag);
                  var after = frag.childNodes.length;
                  var found = document.getElementById('inserted-from-frag');
                  var inDoc = found === div;
                  var parentTag = div.parentNode ? div.parentNode.tagName : 'null';

                  return 'before=' + before +
                         ' firstChildParent=' + firstChildParent +
                         ' after=' + after +
                         ' inDoc=' + inDoc +
                         ' parent=' + parentTag;
                })()
                """));
    }

    [Fact]
    public void AppendingFragmentWithChildDivContainingIframeLeavesSentinelIntact()
    {
        // Matches CreepJS phantom iframe creation pattern:
        // frag = new DocumentFragment(), div appended to frag, innerHTML contains iframe,
        // frag appended to body.
        Assert.Equal(
            "fragEmpty=0 sentinelFound=true frameAppended=true",
            Run("""
                (function () {
                  var frag = new DocumentFragment();
                  var div = document.createElement('div');
                  div.id = 'phantom-container';
                  frag.appendChild(div);
                  div.innerHTML = '<div><iframe id="test-phantom-frame"></iframe></div>';
                  document.body.appendChild(frag);

                  var sentinel = document.getElementById('host') !== null;
                  var frame = document.getElementById('test-phantom-frame') !== null;
                  return 'fragEmpty=' + frag.childNodes.length +
                         ' sentinelFound=' + sentinel +
                         ' frameAppended=' + frame;
                })()
                """));
    }

    [Fact]
    public void SubWindowExposesDocumentFragmentConstructor()
    {
        Assert.Equal(
            "hasCtor=function creates=true",
            Run("""
                (function () {
                  var frame = document.createElement('iframe');
                  document.body.appendChild(frame);
                  var cw = frame.contentWindow;
                  var hasCtor = typeof cw.DocumentFragment;
                  var frag = new cw.DocumentFragment();
                  var creates = frag && frag.nodeType === 11;
                  frame.remove();
                  return 'hasCtor=' + hasCtor + ' creates=' + creates;
                })()
                """));
    }
}
