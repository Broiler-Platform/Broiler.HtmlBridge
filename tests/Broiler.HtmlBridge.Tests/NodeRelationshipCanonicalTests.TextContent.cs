using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>textContent</c> read and write, the mutation records a write delivers to script, and the bindings
/// that read or write an element's text on script's behalf — <c>select.value</c>, <c>script.text</c>,
/// <c>textarea.defaultValue</c>, a frame document's <c>title</c>, the SVG text-content methods — which reached
/// the same bridge helpers <c>element.textContent</c> did and moved with it onto the canonical
/// <c>DomNode.TextContent</c>. <c>select.value</c>'s option-text fallback has since moved again, onto
/// the option's <c>text</c> (HTML §4.10.10: stripped and collapsed, script text left out), which
/// <see cref="OptionTextTests"/> covers.
/// </summary>
/// <remarks>
/// <b>The record tests read the observer's callback synchronously, and that is today's delivery, not
/// Chromium's.</b> The bridge's observer invokes the callback from inside the mutation, one record per
/// call, so the line after <c>el.textContent = 'x'</c> can read the whole log; Chromium queues the records
/// and delivers them at the next microtask checkpoint. The <c>Characterization_</c> tests pin what the
/// synchronous log holds now and say what Chromium's single record holds instead.
/// </remarks>
public partial class NodeRelationshipCanonicalTests
{
    // ── textContent read ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void TextContentIsDescendantTextForAnElementAndDataForCharacterData()
    {
        // Comments contribute nothing to an element's text and text two elements down contributes all of
        // its data, in tree order. A text or comment node answers its own data — a comment is the one
        // node whose textContent is not a concatenation of text nodes.
        Assert.Equal(
            "element=leadboldmiditalend text=lead comment=note childless=empty type=string",
            Run("""
                (function () {
                  var mixed = document.getElementById('mixed');
                  var childless = document.getElementById('empty').textContent;
                  return 'element=' + mixed.textContent +
                         ' text=' + mixed.firstChild.textContent +
                         ' comment=' + mixed.childNodes[1].textContent +
                         ' childless=' + (childless === '' ? 'empty' : childless) +
                         ' type=' + typeof mixed.textContent;
                })()
                """));
    }

    [Fact]
    public void TheDocumentAndItsDoctypeHaveNullTextContent()
    {
        // DOM §4.4 answers null — not the empty string — for exactly these two node kinds. The canonical
        // getter never answers null (a document's is its descendants' text), so this is the distinction
        // a binding has to keep making for itself after the cutover.
        Assert.Equal(
            "document=null doctype=null",
            Run("""
                (function () {
                  return 'document=' + (document.textContent === null ? 'null' : typeof document.textContent) +
                         ' doctype=' + (document.doctype.textContent === null ? 'null' : typeof document.doctype.textContent);
                })()
                """));
    }

    [Fact]
    public void AFragmentsTextContentIsTheTextOfItsDescendants()
    {
        // A fragment is not a document: its textContent is its descendants' text exactly as an element's
        // is, comments skipped. The bridge used to answer the empty string for every fragment, which is how
        // a page checking `fragment.textContent.trim()` before inserting a template sees it as blank.
        Assert.Equal(
            "fragment=xyz",
            Run("""
                (function () {
                  var fragment = document.createDocumentFragment();
                  var div = document.createElement('div'), span = document.createElement('span');
                  div.appendChild(document.createTextNode('x'));
                  span.appendChild(document.createTextNode('y'));
                  div.appendChild(span);
                  fragment.appendChild(div);
                  fragment.appendChild(document.createComment('skipped'));
                  fragment.appendChild(document.createTextNode('z'));
                  return 'fragment=' + fragment.textContent;
                })()
                """));
    }

    // ── textContent write ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void WritingAnElementsTextContentLeavesOneTextNodeOrNone()
    {
        // Replace all (DOM §4.2.3): every child goes, and one text node carrying the value arrives unless
        // the value is empty. The removed child is detached, not merely hidden, and the new text node
        // belongs to the element's document.
        Assert.Equal(
            "children=1 type=3 data=x oldParent=null owner=true emptied=0 emptyText=empty",
            Run("""
                (function () {
                  var three = document.getElementById('three'), old = three.firstChild;
                  three.textContent = 'x';
                  var written = 'children=' + three.childNodes.length +
                                ' type=' + three.firstChild.nodeType +
                                ' data=' + three.firstChild.data +
                                ' oldParent=' + old.parentNode +
                                ' owner=' + (three.firstChild.ownerDocument === document);
                  three.textContent = '';
                  return written + ' emptied=' + three.childNodes.length +
                         ' emptyText=' + (three.textContent === '' ? 'empty' : three.textContent);
                })()
                """));
    }

    [Fact]
    public void WritingNullToTextContentEmptiesAnElementAndAFragment()
    {
        // textContent is a nullable DOMString, so null arrives as null rather than as the string "null",
        // and the setter treats it as the empty string: no children at all. The bridge used to coerce the
        // argument with ToString first and leave one text node reading "null".
        Assert.Equal(
            "element=0 elementText=empty fragment=0",
            Run("""
                (function () {
                  var three = document.getElementById('three');
                  three.textContent = null;
                  var fragment = document.createDocumentFragment();
                  fragment.appendChild(document.createElement('b'));
                  fragment.textContent = null;
                  return 'element=' + three.childNodes.length +
                         ' elementText=' + (three.textContent === '' ? 'empty' : three.textContent) +
                         ' fragment=' + fragment.childNodes.length;
                })()
                """));
    }

    [Fact]
    public void WritingUndefinedToTextContentIsWritingNull()
    {
        // WebIDL converts undefined to IDL null for a nullable DOMString just as it does null, so neither
        // leaves the string "undefined" behind: an element ends with no children and a text node with
        // empty data. The bridge used to coerce undefined with ToString like any other value.
        Assert.Equal(
            "element=0 text=empty",
            Run("""
                (function () {
                  var three = document.getElementById('three');
                  three.textContent = undefined;
                  var text = document.getElementById('mixed').firstChild;
                  text.textContent = undefined;
                  return 'element=' + three.childNodes.length +
                         ' text=' + (text.data === '' ? 'empty' : text.data);
                })()
                """));
    }

    [Fact]
    public void NodePrototypesTextContentSetterReplacesAnElementsAndAFragmentsChildren()
    {
        // Node.prototype's setter is the replace-all the element's and the fragment's own accessors run,
        // so calling it through the descriptor with either as the receiver leaves one text node. It used
        // to be the nodeValue setter, which writes nothing to a node that is not character data, so the
        // old children stayed.
        Assert.Equal(
            "element=#text:x fragment=#text:y",
            Run("""
                (function () {
                  var set = Object.getOwnPropertyDescriptor(Node.prototype, 'textContent').set;
                  var div = document.createElement('div');
                  div.appendChild(document.createElement('b'));
                  set.call(div, 'x');
                  var fragment = document.createDocumentFragment();
                  fragment.appendChild(document.createElement('i'));
                  set.call(fragment, 'y');
                  return 'element=' + describeList(div.childNodes) + ' fragment=' + describeList(fragment.childNodes);
                })()
                """));
    }

    [Fact]
    public void WritingAFragmentsTextContentReplacesItsChildren()
    {
        // The fragment wrapper carries its own setter; it runs the same replace-all an element's does.
        Assert.Equal(
            "children=1 type=3 data=fx emptied=0",
            Run("""
                (function () {
                  var fragment = document.createDocumentFragment();
                  fragment.appendChild(document.createElement('b'));
                  fragment.appendChild(document.createElement('i'));
                  fragment.textContent = 'fx';
                  var written = 'children=' + fragment.childNodes.length +
                                ' type=' + fragment.firstChild.nodeType +
                                ' data=' + fragment.firstChild.data;
                  fragment.textContent = '';
                  return written + ' emptied=' + fragment.childNodes.length;
                })()
                """));
    }

    [Fact]
    public void WritingTextContentOnTheDocumentOrTheDoctypeDoesNothing()
    {
        // DOM §4.4 lists document and doctype as the kinds whose textContent setter does nothing — the
        // canonical setter returns early for both. A write that replaced the document's children would
        // throw (text cannot be a document child) or take the root element with it.
        Assert.Equal(
            "documentChildren=same rootKept=true documentText=null doctypeText=null",
            Run("""
                (function () {
                  var count = document.childNodes.length, root = document.documentElement;
                  document.textContent = 'x';
                  document.doctype.textContent = 'y';
                  return 'documentChildren=' + (document.childNodes.length === count ? 'same' : document.childNodes.length) +
                         ' rootKept=' + (document.documentElement === root) +
                         ' documentText=' + document.textContent +
                         ' doctypeText=' + document.doctype.textContent;
                })()
                """));
    }

    [Fact]
    public void WritingTextContentOnCharacterDataSetsItsData()
    {
        Assert.Equal(
            "text=LEAD nodeValue=LEAD comment=NOTE",
            Run("""
                (function () {
                  var mixed = document.getElementById('mixed');
                  var text = mixed.firstChild, comment = mixed.childNodes[1];
                  text.textContent = 'LEAD';
                  comment.textContent = 'NOTE';
                  return 'text=' + text.data + ' nodeValue=' + text.nodeValue + ' comment=' + comment.data;
                })()
                """));
    }

    [Fact]
    public void WritingNullToACharacterDataNodesTextContentEmptiesItsData()
    {
        // The DOM's textContent setter turns null into the empty string before it looks at the node kind,
        // so a text node's data becomes "" — not "null", which is what ToString-coercing the argument gives.
        Assert.Equal(
            "data=empty",
            Run("""
                (function () {
                  var text = document.getElementById('mixed').firstChild;
                  text.textContent = null;
                  return 'data=' + (text.data === '' ? 'empty' : text.data);
                })()
                """));
    }

    [Fact]
    public void WritingTextContentInAFramesDocumentMintsTheTextNodeInThatDocument()
    {
        // The bridge used to mint the text node from its own factory document and rely on insertion
        // adopting it into the element's; the canonical setter mints it from the element's owner document,
        // so there is no adoption. Both end where Chromium does — owned by the frame's document — for an
        // element the frame created and not yet inserted as well as for one in its tree.
        Assert.Equal(
            "detachedOwner=true attachedOwner=true attachedText=sub2",
            Run("""
                (function () {
                  var frameDocument = document.getElementById('f').contentDocument;
                  var created = frameDocument.createElement('p');
                  created.textContent = 'sub';
                  var inTree = frameDocument.getElementById('fp');
                  inTree.textContent = 'sub2';
                  return 'detachedOwner=' + (created.firstChild.ownerDocument === frameDocument) +
                         ' attachedOwner=' + (inTree.firstChild.ownerDocument === frameDocument) +
                         ' attachedText=' + inTree.textContent;
                })()
                """,
                FramePageHtml));
    }

    // ── mutation records for a write ───────────────────────────────────────────────────────────

    [Fact]
    public void Characterization_ATextWriteOverThreeChildrenDeliversThreeRemovalsThenAnAddition()
    {
        // The bridge's per-node split, not Chromium. Chromium queues ONE record for the whole replace-all:
        // {addedNodes: [#text], removedNodes: [3 spans], previousSibling: null, nextSibling: null}. The
        // canonical textContent setter publishes exactly that record, and the observer binding splits it
        // into one JS record per node, the removals first and then the addition, each carrying the
        // record's null siblings.
        //
        // What changed at the cutover: the two `next=` values on the first two removals. The bridge used
        // to clear the children one RemoveChild at a time and then append the text node, so four
        // canonical records arrived, each with the siblings its own step saw — `next=SPAN:2` and
        // `next=SPAN:3`. One replace-all record has no per-removal siblings to carry, and null is what
        // Chromium reports for it. The count and the order did not change: the binding delivers a
        // record's removed nodes before its added nodes, which is the order the replace-all performs
        // them in, so script still sees three removals and then the addition.
        Assert.Equal(
            "calls=4 " +
            "{childList added= removed=SPAN:1 prev=null next=null}," +
            "{childList added= removed=SPAN:2 prev=null next=null}," +
            "{childList added= removed=SPAN:3 prev=null next=null}," +
            "{childList added=#text:x removed= prev=null next=null}",
            Run("""
                (function () {
                  var three = document.getElementById('three'), log = [], calls = 0;
                  new MutationObserver(function (records) {
                    calls++;
                    for (var i = 0; i < records.length; i++) log.push(describeRecord(records[i]));
                  }).observe(three, { childList: true });
                  three.textContent = 'x';
                  return 'calls=' + calls + ' ' + log.join(',');
                })()
                """));
    }

    [Fact]
    public void Characterization_AnEmptyTextWriteOverThreeChildrenDeliversThreeRemovals()
    {
        // The bridge's per-node split, not Chromium. Chromium queues one record carrying all three removed
        // nodes and null siblings; the bridge delivers one JS record per removed node out of that record.
        //
        // What changed at the cutover: the first two removals report `next=null` where they reported
        // `next=SPAN:2` and `next=SPAN:3`. Each removal used to be its own RemoveChild, carrying the next
        // sibling still there when that child went; the canonical setter publishes one replace-all record
        // whose siblings are null, as Chromium's is. The count and the order are unchanged.
        Assert.Equal(
            "calls=3 " +
            "{childList added= removed=SPAN:1 prev=null next=null}," +
            "{childList added= removed=SPAN:2 prev=null next=null}," +
            "{childList added= removed=SPAN:3 prev=null next=null}",
            Run("""
                (function () {
                  var three = document.getElementById('three'), log = [], calls = 0;
                  new MutationObserver(function (records) {
                    calls++;
                    for (var i = 0; i < records.length; i++) log.push(describeRecord(records[i]));
                  }).observe(three, { childList: true });
                  three.textContent = '';
                  return 'calls=' + calls + ' ' + log.join(',');
                })()
                """));
    }

    [Fact]
    public void AnEmptyTextWriteOnAChildlessElementQueuesNoRecord()
    {
        // Replace all queues a record only when something was added or removed. This holds for Chromium
        // and for today's delivery alike. The test pins that observable outcome, not what the canonical
        // setter publishes: the observer binding delivers nothing for a child-list record with both node
        // lists empty, so an empty record would read the same here.
        Assert.Equal(
            "calls=0 pending=0",
            Run("""
                (function () {
                  var empty = document.getElementById('empty'), calls = 0;
                  var observer = new MutationObserver(function () { calls++; });
                  observer.observe(empty, { childList: true });
                  empty.textContent = '';
                  return 'calls=' + calls + ' pending=' + observer.takeRecords().length;
                })()
                """));
    }

    [Fact]
    public void Characterization_ReplaceChildDeliversARemovalThenAnAdditionAsTwoRecords()
    {
        // Today's delivery, not Chromium. Chromium queues one record for replaceChild carrying both the
        // added and the removed node, with the replaced child's two siblings. The canonical ReplaceChild
        // is a RemoveChild followed by an InsertBefore, which publish separately, so the removal arrives
        // first as a record of its own. Because the two are separate records, the order in which the
        // observer binding splits a single record's added and removed nodes (removals first) never
        // reaches this case.
        Assert.Equal(
            "calls=2 " +
            "{childList added= removed=B:b prev=B:a next=B:c}," +
            "{childList added=I:X removed= prev=B:a next=B:c}",
            Run("""
                (function () {
                  var swap = document.getElementById('swap'), log = [], calls = 0;
                  var incoming = document.createElement('i');
                  incoming.appendChild(document.createTextNode('X'));
                  new MutationObserver(function (records) {
                    calls++;
                    for (var i = 0; i < records.length; i++) log.push(describeRecord(records[i]));
                  }).observe(swap, { childList: true });
                  swap.replaceChild(incoming, swap.childNodes[1]);
                  return 'calls=' + calls + ' ' + log.join(',');
                })()
                """));
    }

    // ── bindings that read or write an element's text ──────────────────────────────────────────

    [Fact]
    public void ASelectsValueFallsBackToItsOptionsTextAndMatchesByIt()
    {
        // An option without a value attribute is valued by its text — read for select.value, compared
        // for a select.value write — and that text is the option's descendant text as it is now, not as
        // it was parsed.
        Assert.Equal(
            "initial=First rewritten=Eins byValue=1 byText=0",
            Run("""
                (function () {
                  var select = document.getElementById('sel');
                  var initial = select.value;
                  document.getElementById('o1').textContent = 'Eins';
                  var rewritten = select.value;
                  select.value = 'v2';
                  var byValue = select.selectedIndex;
                  select.value = 'Eins';
                  return 'initial=' + initial + ' rewritten=' + rewritten +
                         ' byValue=' + byValue + ' byText=' + select.selectedIndex;
                })()
                """));
    }

    [Fact]
    public void AnOptionsTextReadsAndWritesItsChildText()
    {
        // HTMLOptionElement.text: the option's text on read, and a replace-all of its children on write.
        // This was skipped while the bridge installed no `text` on an option (the read was undefined and
        // the write landed as an expando); OptionTextTests covers the member in full.
        Assert.Equal(
            "read=First written=Uno children=1",
            Run("""
                (function () {
                  var option = document.getElementById('o1');
                  var read = option.text;
                  option.text = 'Uno';
                  return 'read=' + read + ' written=' + option.textContent + ' children=' + option.childNodes.length;
                })()
                """));
    }

    [Fact]
    public void TextAliasesReadAndWriteTheSameChildText()
    {
        // innerText and outerText read through the same text value textContent does (the bridge has no
        // layout-aware innerText; for an inline element with plain text Chromium's answer is the same),
        // and a script element's `text` writes through the same replace-all as textContent.
        Assert.Equal(
            "innerText=bold outerText=bold scriptText=alert(1) scriptChildren=1 scriptTextContent=alert(1)",
            Run("""
                (function () {
                  var bold = document.getElementById('m1');
                  var script = document.createElement('script');
                  script.type = 'text/plain';
                  script.appendChild(document.createTextNode('old'));
                  script.text = 'alert(1)';
                  return 'innerText=' + bold.innerText + ' outerText=' + bold.outerText +
                         ' scriptText=' + script.text + ' scriptChildren=' + script.childNodes.length +
                         ' scriptTextContent=' + script.textContent;
                })()
                """));
    }

    [Fact]
    public void ATextareasDefaultValueIsItsChildText()
    {
        // A textarea's default value lives in its child text, so writing defaultValue replaces that text,
        // and an undirtied control reports the new default as its value.
        Assert.Equal(
            "before=orig text=fresh children=1 value=fresh",
            Run("""
                (function () {
                  var area = document.getElementById('ta');
                  var before = area.defaultValue;
                  area.defaultValue = 'fresh';
                  return 'before=' + before + ' text=' + area.textContent +
                         ' children=' + area.childNodes.length + ' value=' + area.value;
                })()
                """));
    }

    [Fact]
    public void AFramesDocumentTitleReadsAndWritesItsTitleElement()
    {
        // A frame document's title is its own binding, separate from the page's: it reads the <title>
        // element's text and writes by replacing that element's children, so the element and the
        // document agree afterwards and the new text node belongs to the frame.
        Assert.Equal(
            "before=inner after=changed element=changed children=1 owner=true",
            Run("""
                (function () {
                  var frameDocument = document.getElementById('f').contentDocument;
                  var before = frameDocument.title;
                  frameDocument.title = 'changed';
                  var title = frameDocument.getElementsByTagName('title')[0];
                  return 'before=' + before + ' after=' + frameDocument.title +
                         ' element=' + title.textContent + ' children=' + title.childNodes.length +
                         ' owner=' + (title.firstChild.ownerDocument === frameDocument);
                })()
                """,
                FramePageHtml));
    }

    [Fact]
    public void SvgTextContentMethodsCountTheElementsDescendantText()
    {
        // The SVGTextContentElement methods count characters over the element's descendant text, a
        // <tspan>'s included. Only the count and the index bound are asserted: the lengths and positions
        // are font-size estimates here, where Chromium measures glyphs.
        Assert.Equal(
            "chars=7 lastIndexOk=true pastEndThrows=true",
            Run("""
                (function () {
                  var text = document.getElementById('svgText'), threw = false, ok = true;
                  try { text.getStartPositionOfChar(6); } catch (e) { ok = false; }
                  try { text.getStartPositionOfChar(7); } catch (e) { threw = true; }
                  return 'chars=' + text.getNumberOfChars() + ' lastIndexOk=' + ok + ' pastEndThrows=' + threw;
                })()
                """));
    }
}
