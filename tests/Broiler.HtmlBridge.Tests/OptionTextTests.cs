using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>HTMLOptionElement.text</c> and the option value it stands in for, asserted from page script.
/// <para>
/// HTML §4.10.10: an option's <c>text</c> is the concatenation of its descendant text nodes in tree
/// order — skipping any inside a descendant <c>script</c>, HTML or SVG, and a shadow tree, which is not a
/// descendant at all — with ASCII whitespace stripped from both ends and every inner run collapsed to one
/// space. Setting it is a "string replace all", the same replace-all a <c>textContent</c> write is. It is
/// also what an option is worth when it has no <c>value</c> attribute, which is why <c>option.value</c>,
/// <c>select.value</c>, <c>select.value = …</c> and the <c>FormData</c> entry are pinned here too: a
/// select whose options are written <c>&lt;option&gt;\n  Red\n&lt;/option&gt;</c> is valued "Red" in a
/// browser, not by its raw text.
/// </para>
/// <para>
/// <b>Why the member had to exist rather than fall through.</b> An option wrapper had no <c>text</c> at
/// all, so the read answered <c>undefined</c> and a write landed as a plain expando that changed nothing
/// — the classic <c>select.options[i].text = label</c> rename silently did nothing, and a script building
/// a label list from <c>option.text</c> built a list of <c>"undefined"</c>.
/// </para>
/// </summary>
/// <remarks>
/// The <c>Characterization_</c> test pins a mutation-record sequence that is today's answer, not
/// Chromium's, and says what Chromium's is.
/// </remarks>
public class OptionTextTests
{
    private const string PageUrl = "https://example.test/option-text";

    /// <summary>
    /// <c>#spaced</c> surrounds and splits its words with runs of spaces, tabs and line feeds and holds a
    /// no-break space, which is not ASCII whitespace and so survives. <c>#nested</c> splits its text
    /// across elements. <c>#scripted</c> holds an inert <c>text/plain</c> script, whose text the option's
    /// <c>text</c> leaves out. <c>#two</c> is the rename target with two children. <c>#valued</c> has a
    /// <c>value</c> attribute, which wins over its padded text, and <c>#placeholder</c> an empty one, which
    /// wins too. <c>#shadowed</c> holds a declarative shadow root whose text is not the option's.
    /// </summary>
    private const string PageHtml =
        "<html><body>" +
        "<form id=\"form\">" +
        "<select id=\"sel\" name=\"choice\">" +
        "<option id=\"spaced\">\n\t  Two \t\n  words\u00a0here \n </option>" +
        "<option id=\"nested\">A<b>B<i>C</i></b> D</option>" +
        "<option id=\"scripted\">Keep<script type=\"text/plain\">drop</script> this</option>" +
        "<option id=\"two\">one<b>two</b></option>" +
        "<option id=\"valued\" value=\"v\">  Shown  </option>" +
        "<option id=\"placeholder\" value=\"\">  Pick  </option>" +
        "<option id=\"shadowed\">Red<span><template shadowrootmode=\"open\"><style>b{}</style>X</template></span></option>" +
        "</select>" +
        "</form>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// Page-global helpers: <c>show</c> spells a string's whitespace visibly (a tab as <c>\t</c>, a line
    /// feed as <c>\n</c>, a form feed as <c>\f</c>, a no-break space as <c>\u00a0</c>) so an assertion can
    /// tell "stripped" from "kept" — spelling the no-break space also keeps it out of <c>#out</c>'s
    /// serialization, which would write it back as <c>&amp;nbsp;</c>. <c>describeRecord</c> spells one
    /// <c>MutationRecord</c> the way <see cref="NodeRelationshipCanonicalTests"/> does.
    /// </summary>
    private const string Helpers = """
        function show(s) {
          return '[' + JSON.stringify(String(s)).slice(1, -1).split('\u00a0').join('\\u00a0') + ']';
        }
        function describeNode(node) {
          if (node === null) return 'null';
          if (node.nodeType === 3) return '#text:' + node.data;
          return node.nodeName + ':' + node.textContent;
        }
        function describeList(list) {
          var names = [];
          for (var i = 0; i < list.length; i++) names.push(describeNode(list[i]));
          return names.join('|');
        }
        function describeRecord(record) {
          return '{' + record.type + ' added=' + describeList(record.addedNodes) +
                 ' removed=' + describeList(record.removedNodes) +
                 ' prev=' + describeNode(record.previousSibling) +
                 ' next=' + describeNode(record.nextSibling) + '}';
        }
        """;

    /// <summary>
    /// Runs <see cref="Helpers"/> and then <paramref name="script"/> against the fixture and returns what
    /// it wrote to <c>#out</c> — the same public-surface read <see cref="NodeTreeIdentityTests"/> uses. A
    /// throw is written there too, as <see cref="NodeRelationshipCanonicalTests"/> does: a member that is
    /// missing or throws would otherwise leave <c>#out</c> empty, with no word of where.
    /// </summary>
    private static string Run(string script)
    {
        var html = new ScriptEngine().Execute(
            [
                Helpers,
                "var probeResult;" +
                $"try {{ probeResult = String({script}); }} " +
                "catch (e) { probeResult = 'threw ' + (e && e.name) + ': ' + (e && e.message); }" +
                "document.getElementById('out').textContent = probeResult;",
            ],
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
    public void TextStripsAndCollapsesAsciiWhitespaceButKeepsANoBreakSpace()
    {
        // CHANGED: the member did not exist, so `text` was undefined and this script threw.
        // The two options built by script go through no parser rule: in `built` a lone tab, form feed and
        // carriage return each become one space and a no-break space at either end is text, so it stays;
        // in `split` one whitespace run starts in a text node and ends inside a <b>, and collapses as one.
        Assert.Equal(
            "type=string text=[Two words\\u00a0here] textContentUntouched=true " +
            "built=[\\u00a0 a b c d \\u00a0] split=[a b]",
            Run("""
                (function () {
                  var o = document.getElementById('spaced');
                  var text = o.text;
                  var built = document.createElement('option');
                  built.textContent = '\u00a0 a\tb\fc';
                  built.appendChild(document.createTextNode('\r d \u00a0'));
                  var split = document.createElement('option');
                  split.appendChild(document.createTextNode('a\t'));
                  split.appendChild(document.createElement('b')).appendChild(document.createTextNode('\n\f b'));
                  return 'type=' + typeof text +
                         ' text=' + show(text) +
                         ' textContentUntouched=' + (o.textContent.indexOf('\t') >= 0) +
                         ' built=' + show(built.text) +
                         ' split=' + show(split.text);
                })()
                """));
    }

    [Fact]
    public void TextReadsTheTextOfNestedElementsInTreeOrder()
    {
        // `structure` is the parser keeping <b> and <i> inside the option; without it the option would
        // hold one text node and this would not be reading nested text at all.
        Assert.Equal(
            "structure=true text=[ABC D]",
            Run("""
                (function () {
                  var nested = document.getElementById('nested');
                  return 'structure=' + (nested.querySelector('b i') !== null) + ' text=' + show(nested.text);
                })()
                """));
    }

    [Fact]
    public void TextLeavesOutTheTextOfHtmlAndSvgScriptDescendants()
    {
        // The parsed script is a direct child; the two added below are an HTML script nested inside an
        // element and an SVG script (created in the SVG namespace), both left out with everything inside
        // them. An SVG element that is not a script contributes its text as any element does. `others`
        // holds what is not an HTML or SVG script element and so counts — a MathML <script> and an XHTML
        // element whose local name is "SCRIPT" — beside a fragment-parsed HTML script, which does not.
        Assert.Equal(
            "parsed=[Keep this] nested=[Keep this too svg-title] textContentHasScripts=true " +
            "others=[a math upper b]",
            Run("""
                (function () {
                  var o = document.getElementById('scripted');
                  var parsed = o.text;
                  var svgNs = 'http://www.w3.org/2000/svg';
                  var wrapper = document.createElement('span');
                  var htmlScript = document.createElement('script');
                  htmlScript.type = 'text/plain';
                  htmlScript.appendChild(document.createTextNode('html-script'));
                  wrapper.appendChild(document.createTextNode(' too '));
                  wrapper.appendChild(htmlScript);
                  var svgScript = document.createElementNS(svgNs, 'script');
                  svgScript.appendChild(document.createElementNS(svgNs, 'g'))
                           .appendChild(document.createTextNode('svg-script'));
                  var svgTitle = document.createElementNS(svgNs, 'title');
                  svgTitle.appendChild(document.createTextNode('svg-title'));
                  wrapper.appendChild(svgScript);
                  wrapper.appendChild(svgTitle);
                  o.appendChild(wrapper);
                  var content = o.textContent;

                  var others = document.createElement('option');
                  others.appendChild(document.createTextNode('a'));
                  others.appendChild(document.createElementNS('http://www.w3.org/1998/Math/MathML', 'script'))
                        .appendChild(document.createTextNode(' math'));
                  others.appendChild(document.createElementNS('http://www.w3.org/1999/xhtml', 'SCRIPT'))
                        .appendChild(document.createTextNode(' upper'));
                  others.appendChild(document.createElement('span')).innerHTML =
                      ' b<script type="text/plain">fragment-script</script>';
                  return 'parsed=' + show(parsed) + ' nested=' + show(o.text) +
                         ' textContentHasScripts=' + (content.indexOf('drop') >= 0 &&
                                                      content.indexOf('html-script') >= 0 &&
                                                      content.indexOf('svg-script') >= 0) +
                         ' others=' + show(others.text);
                })()
                """));
    }

    [Fact]
    public void TextLeavesOutTheTextOfAShadowTreeInsideTheOption()
    {
        // CHANGED: the bridge keeps a shadow host's #shadow-root in the host's child list and the walk went
        // into it, so a shadow tree's text — its <style> included — became the option's text and, with no
        // value attribute, its value, select.value's match and the FormData entry. A shadow tree is not
        // the option's descendant (DOM §4.2.2); Chromium answers "Red" and "Blue" throughout.
        Assert.Equal(
            "shadowRoots=true declarative=[Red] declarativeValue=[Red] imperative=[Blue] " +
            "imperativeValue=[Blue] selectedBy=7 selectValue=[Blue] formValue=[Blue]",
            Run("""
                (function () {
                  var select = document.getElementById('sel');
                  var declarative = document.getElementById('shadowed');
                  var imperative = document.createElement('option');
                  imperative.appendChild(document.createTextNode('Blue'));
                  var host = imperative.appendChild(document.createElement('span'));
                  host.attachShadow({ mode: 'open' }).innerHTML = '<style>b{}</style>X';
                  select.appendChild(imperative);
                  var shadowRoots = declarative.children[0].shadowRoot !== null && host.shadowRoot !== null;
                  select.value = 'Blue';
                  return 'shadowRoots=' + shadowRoots +
                         ' declarative=' + show(declarative.text) +
                         ' declarativeValue=' + show(declarative.value) +
                         ' imperative=' + show(imperative.text) +
                         ' imperativeValue=' + show(imperative.value) +
                         ' selectedBy=' + select.selectedIndex +
                         ' selectValue=' + show(select.value) +
                         ' formValue=' + show(new FormData(document.getElementById('form')).get('choice'));
                })()
                """));
    }

    [Fact]
    public void OnlyAnHtmlOptionElementHasTextOrIsValuedByIt()
    {
        // An SVG <option> and an XHTML element whose local name is "OPTION" are not HTMLOptionElements
        // (Chromium: an SVGElement and an HTMLUnknownElement), so neither has `text` and neither is valued
        // by its text.
        Assert.Equal(
            "svgText=undefined svgTextValued=false upperText=undefined upperTextValued=false " +
            "htmlText=string htmlValue=[a b]",
            Run("""
                (function () {
                  var svg = document.createElementNS('http://www.w3.org/2000/svg', 'option');
                  var upper = document.createElementNS('http://www.w3.org/1999/xhtml', 'OPTION');
                  var html = document.createElementNS('http://www.w3.org/1999/xhtml', 'option');
                  svg.textContent = upper.textContent = html.textContent = ' a  b ';
                  return 'svgText=' + typeof svg.text + ' svgTextValued=' + (svg.value === 'a b') +
                         ' upperText=' + typeof upper.text + ' upperTextValued=' + (upper.value === 'a b') +
                         ' htmlText=' + typeof html.text + ' htmlValue=' + show(html.value);
                })()
                """));
    }

    [Fact]
    public void SettingTextReplacesEveryChildWithOneTextNode()
    {
        // Setting text is a string replace all: one text node holding the string as written, so the
        // whitespace the getter strips is still there in the node, and no node at all for the empty
        // string. `text` is a plain DOMString, so null is the string "null" (unlike textContent, which is
        // nullable).
        Assert.Equal(
            "children=1 kind=3 content=[  Uno  dos ] text=[Uno dos] nullWrite=[null] emptyWrite=0 expando=false",
            Run("""
                (function () {
                  var o = document.getElementById('two');
                  o.text = '  Uno  dos ';
                  var answer = 'children=' + o.childNodes.length +
                               ' kind=' + o.firstChild.nodeType +
                               ' content=' + show(o.textContent) +
                               ' text=' + show(o.text);
                  o.text = null;
                  answer += ' nullWrite=' + show(o.textContent);
                  o.text = '';
                  // A write that only made a data property would leave one behind on the wrapper.
                  var own = Object.getOwnPropertyDescriptor(o, 'text');
                  return answer + ' emptyWrite=' + o.childNodes.length +
                         ' expando=' + !!(own && 'value' in own);
                })()
                """));
    }

    [Fact]
    public void TheTextSetterWithoutAnArgumentThrowsATypeErrorAndLeavesTheChildren()
    {
        // WebIDL: an attribute setter called with no argument at all — reachable only through the property
        // descriptor — throws the arity TypeError before anything is converted or replaced.
        Assert.Equal(
            "threw=TypeError children=2 text=[onetwo]",
            Run("""
                (function () {
                  var o = document.getElementById('two');
                  var setter = Object.getOwnPropertyDescriptor(o, 'text').set;
                  var threw = 'nothing';
                  try { setter.call(o); } catch (e) { threw = e.name; }
                  return 'threw=' + threw + ' children=' + o.childNodes.length + ' text=' + show(o.text);
                })()
                """));
    }

    [Fact]
    public void Characterization_SettingTextDeliversTheReplaceAllAsRemovalsThenTheAddition()
    {
        // The bridge's per-node split of one canonical replace-all record, removals first, each carrying
        // the record's null siblings — the same sequence a textContent write delivers
        // (NodeRelationshipCanonicalTests). Chromium queues ONE record for the whole write:
        // {addedNodes: [#text], removedNodes: [#text:one, B], previousSibling: null, nextSibling: null}.
        // The log is read synchronously, which is also today's delivery and not Chromium's: the bridge
        // invokes the callback from inside the mutation, while Chromium runs it at the next microtask
        // checkpoint, so this script answers "calls=0 " there and the callback receives that one record
        // afterwards.
        Assert.Equal(
            "calls=3 " +
            "{childList added= removed=#text:one prev=null next=null}," +
            "{childList added= removed=B:two prev=null next=null}," +
            "{childList added=#text:x removed= prev=null next=null}",
            Run("""
                (function () {
                  var o = document.getElementById('two'), log = [], calls = 0;
                  new MutationObserver(function (records) {
                    calls++;
                    for (var i = 0; i < records.length; i++) log.push(describeRecord(records[i]));
                  }).observe(o, { childList: true });
                  o.text = 'x';
                  return 'calls=' + calls + ' ' + log.join(',');
                })()
                """));
    }

    [Fact]
    public void AnOptionWithoutAValueAttributeIsValuedByItsText()
    {
        // CHANGED: select.value read and matched an option by its raw textContent, whitespace and script
        // text included, and option.value answered "" — so a select written with indented options could
        // be neither read nor set by the value a browser reports. FormData collects the select's value, so
        // it answers the same text. A value attribute wins whenever it is present, even when empty: the
        // placeholder is valued "", not "Pick", and select.value = '' selects it.
        Assert.Equal(
            "optionValue=[Two words\\u00a0here] selectValue=[Two words\\u00a0here] selectedBy=0 " +
            "formValue=[Two words\\u00a0here] scriptedValue=[Keep this] scriptedFormValue=[Keep this] " +
            "valuedOption=[v] valuedText=[Shown] placeholderValue=[] placeholderText=[Pick] " +
            "selectedByEmpty=5 emptySelectValue=[]",
            Run("""
                (function () {
                  var select = document.getElementById('sel');
                  var form = document.getElementById('form');
                  var spaced = document.getElementById('spaced');
                  var answer = 'optionValue=' + show(spaced.value) + ' selectValue=' + show(select.value);
                  select.selectedIndex = 3;
                  select.value = spaced.text;
                  answer += ' selectedBy=' + select.selectedIndex +
                            ' formValue=' + show(new FormData(form).get('choice'));
                  select.value = 'Keep this';
                  answer += ' scriptedValue=' + show(select.value) +
                            ' scriptedFormValue=' + show(new FormData(form).get('choice'));
                  var valued = document.getElementById('valued');
                  var placeholder = document.getElementById('placeholder');
                  answer += ' valuedOption=' + show(valued.value) + ' valuedText=' + show(valued.text) +
                            ' placeholderValue=' + show(placeholder.value) +
                            ' placeholderText=' + show(placeholder.text);
                  select.value = '';
                  return answer + ' selectedByEmpty=' + select.selectedIndex +
                         ' emptySelectValue=' + show(select.value);
                })()
                """));
    }
}
