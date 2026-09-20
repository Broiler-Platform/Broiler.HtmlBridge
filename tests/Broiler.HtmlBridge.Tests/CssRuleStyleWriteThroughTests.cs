using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A write to a style rule's <c>style</c> declaration — <c>setProperty</c>, <c>removeProperty</c>,
/// <c>cssText</c>, <c>cssFloat</c> and the camel-cased attributes — asserted from page script against the
/// cascade it is supposed to move, and against the style text the renderer is handed.
/// <para>
/// CSSOM §6.4.3: a <c>CSSStyleRule</c>'s <c>style</c> is the rule's own declaration block, not a copy of
/// it, so a write to it changes the rule, and so the cascade. The bridge's declaration was a detached map
/// built from the rule's serialized block: every write answered back through <c>rule.style</c> and
/// <c>rule.cssText</c>, and nothing else in the page saw it — <c>getComputedStyle</c>, layout and paint all
/// kept the rule as authored. A test that read only <c>rule.style</c> after a write passed against that.
/// </para>
/// <para>
/// Every test that expects a change reads the computed value <em>before</em> the write too. A computed-style
/// memo that kept its first read (<see cref="ComputedStyleCssomInvalidationTests"/>) would then answer the
/// pre-write value, so such a test fails rather than passing by never having read anything to go stale.
/// </para>
/// <para>
/// CSSOM's own rules for the edit are asserted where the write now reaches the cascade, because each one that
/// a detached map shrugged off now changes what renders: a property name is ASCII-lowercased and never
/// rewritten from camel case, a priority is <c>important</c> or nothing, a value never carries
/// <c>!important</c>, a custom property has no camel-cased attribute, and a logical box shorthand is not the
/// physical sides the engine expands it to.
/// </para>
/// <para>
/// Tests named <c>Characterization_</c> pin where writes deliberately still stay on the object, each with
/// what Chromium does instead; the rest assert Chromium's answer.
/// </para>
/// </summary>
public class CssRuleStyleWriteThroughTests
{
    private const string PageUrl = "https://example.test/css-rule-style";

    /// <summary>
    /// <c>#author</c> gives <c>#probe</c> a starting <c>display</c> and <c>color</c> that differ from what
    /// <c>span.box</c> gives it, so removing <c>#probe</c>'s declarations shows up as a second, different
    /// answer. The other sheets each serve one test: <c>#nested</c> puts style rules one and two grouping
    /// levels deep; <c>#group</c> is a grouping rule a page inserts into; <c>#descriptors</c> holds the
    /// declaration blocks that are not style rules; <c>#dup</c> repeats a property; <c>#box</c> is
    /// written through shorthands; <c>#vars</c> declares two custom properties one camel-case rewrite apart;
    /// <c>#logical</c> sets margins through a logical shorthand, alone and ahead of a physical side.
    /// </summary>
    private const string PageHtml =
        "<!DOCTYPE html><html><head>" +
        "<style id=\"author\">#probe { display: block; color: rgb(255, 0, 0); } " +
        "span.box { display: inline; color: rgb(0, 0, 255); }</style>" +
        "<style id=\"nested\">@media all { @supports (display: grid) { #deep { display: block; } } " +
        "#sib { display: block; } } @supports (display: block) { #other { display: block; } }</style>" +
        "<style id=\"group\">@media all { .unused { color: rgb(1, 2, 3); } }</style>" +
        "<style id=\"descriptors\">@font-face { font-family: Probe; src: local(Arial); } " +
        "@page { margin-top: 1in; } @keyframes spin { from { opacity: 1; } }</style>" +
        "<style id=\"dup\">#dup { display: -webkit-box; display: flex; }</style>" +
        "<style id=\"box\">#box { margin: 1px; }</style>" +
        "<style id=\"vars\">#vars { --myVar: 1; --my-var: 2; }</style>" +
        "<style id=\"logical\">#logical { margin-inline: 10px; } #logical2 { margin-inline: 10px; margin-left: 3px; }</style>" +
        "</head><body><span id=\"probe\" class=\"box\">x</span>" +
        "<span id=\"deep\"></span><span id=\"sib\"></span><span id=\"other\"></span>" +
        "<span id=\"dup\"></span><div id=\"box\"></div><div id=\"logical\"></div><div id=\"logical2\"></div>" +
        "<div id=\"out\"></div></body></html>";

    /// <summary>
    /// <c>sheetOf</c> finds an element-backed sheet by its owner's <c>id</c>; <c>read</c> spells
    /// <c>#probe</c>'s computed <c>display</c> and <c>color</c> as one token; <c>displayOf</c> reads one
    /// element's computed <c>display</c>. Each reads a fresh declaration, as a page would.
    /// </summary>
    private const string Helpers = """
        function sheetOf(id) {
          var list = document.styleSheets;
          for (var i = 0; i < list.length; i++) {
            var s = list[i] || list.item(i);
            if (s && s.ownerNode && s.ownerNode.id === id) return s;
          }
          return null;
        }
        function read() {
          var cs = getComputedStyle(document.getElementById('probe'));
          return cs.display + '/' + cs.color;
        }
        function displayOf(id) {
          return getComputedStyle(document.getElementById(id)).display;
        }
        """;

    /// <summary>
    /// The serialized page after <see cref="Helpers"/> and <paramref name="script"/> ran. The tests that
    /// read the style text as well as <c>#out</c> run through this rather than <see cref="Run"/>.
    /// </summary>
    private static string RunHtml(string script) =>
        PageProbe.Render([Helpers, PageProbe.GuardedProbe(script)], PageHtml, PageUrl);

    /// <summary>What <paramref name="script"/> wrote to <c>#out</c>, with a throw written there instead.</summary>
    private static string Run(string script) => OutOf(RunHtml(script));

    private static string OutOf(string html) => PageProbe.OutOf(html);

    private static string StyleTextOf(string html, string id) => PageProbe.StyleTextOf(html, id);

    [Fact]
    public void AHeldDeclarationKeepsWritingToItsRuleAfterInsertAndDeleteMoveIt()
    {
        // The declaration a page holds addresses its rule, not an index: two inserts ahead of it and a
        // delete between them leave #probe's rule at index 1, and writes through the held object still
        // land on that rule, which is also what cssRules[1] answers afterwards. parentRule stays the rule
        // object the declaration came from.
        Assert.Equal(
            "before=block/rgb(255, 0, 0) after=flex/rgb(0, 128, 0) reread=flex " +
            "cssText=#probe { display: flex; color: rgb(0, 128, 0); } parent=true",
            Run("""
                (function () {
                  var s = sheetOf('author'), rule = s.cssRules[0], st = rule.style;
                  var before = read();
                  s.insertRule('.x { color: red; }', 0);
                  s.insertRule('.y { color: red; }', 0);
                  s.deleteRule(1);
                  st.setProperty('display', 'flex');
                  st.color = 'rgb(0, 128, 0)';
                  return 'before=' + before + ' after=' + read() + ' reread=' + s.cssRules[1].style.display +
                         ' cssText=' + s.cssRules[1].cssText + ' parent=' + (st.parentRule === rule);
                })()
                """));
    }

    [Fact]
    public void DeclarationsFromTwoReadsOfCssRulesWriteToTheSameRule()
    {
        // Chromium hands back one object for both reads. The bridge builds a rule object per read, so the
        // two declarations are different objects — and a write through either must keep the other's
        // earlier write rather than overwrite the rule with its own stale copy of it.
        Assert.Equal(
            "before=block/rgb(255, 0, 0) after=grid/rgb(0, 128, 0) b=grid|rgb(0, 128, 0) " +
            "cssText=#probe { display: grid; color: rgb(0, 128, 0); }",
            Run("""
                (function () {
                  var s = sheetOf('author');
                  var a = s.cssRules[0].style, b = s.cssRules[0].style;
                  var before = read();
                  a.setProperty('color', 'rgb(0, 128, 0)');
                  b.setProperty('display', 'flex');
                  a.setProperty('display', 'grid');
                  return 'before=' + before + ' after=' + read() +
                         ' b=' + b.getPropertyValue('display') + '|' + b.getPropertyValue('color') +
                         ' cssText=' + s.cssRules[0].cssText;
                })()
                """));
    }

    [Fact]
    public void AnInvalidOrUnchangedWriteLeavesTheSheetAsAuthored()
    {
        // CSSOM: a value that does not parse for the property is ignored, and so is a write of the value
        // already there, or a removal of a property the rule does not declare. None of them is an edit, so
        // the renderer must still be handed the author's own text byte for byte rather than the sheet
        // re-serialized from its rule model.
        var html = RunHtml("""
            (function () {
              var rule = sheetOf('author').cssRules[0];
              var before = read();
              rule.style.setProperty('display', 'bogus');
              rule.style.display = 'bogus';
              rule.style.cssFloat = 'sideways';
              rule.style.setProperty('display', 'block');
              rule.style.removeProperty('opacity');
              return 'before=' + before + ' after=' + read() + ' display=' + rule.style.display +
                     ' cssText=' + rule.cssText;
            })()
            """);

        Assert.Equal(
            "before=block/rgb(255, 0, 0) after=block/rgb(255, 0, 0) display=block " +
            "cssText=#probe { display: block; color: rgb(255, 0, 0); }",
            OutOf(html));
        Assert.Equal(
            "#probe { display: block; color: rgb(255, 0, 0); } span.box { display: inline; color: rgb(0, 0, 255); }",
            StyleTextOf(html, "author"));
    }

    [Fact]
    public void AnEmptyValueRemovesTheDeclaration()
    {
        // setProperty(p, '') is removeProperty(p), and so is assigning '' to the attribute; with both of
        // #probe's declarations gone, span.box's apply.
        Assert.Equal(
            "before=block/rgb(255, 0, 0) after=inline/rgb(0, 0, 255) cssText=[]",
            Run("""
                (function () {
                  var rule = sheetOf('author').cssRules[0];
                  var before = read();
                  rule.style.setProperty('display', '');
                  rule.style.color = '';
                  return 'before=' + before + ' after=' + read() + ' cssText=[' + rule.style.cssText + ']';
                })()
                """));
    }

    [Fact]
    public void ImportanceIsWrittenAndClearedAgainstAnInlineDeclaration()
    {
        // An !important rule declaration beats a normal inline one, and setting the same property again
        // without a priority clears the flag, so the inline declaration wins again.
        Assert.Equal(
            "inline=rgb(128, 0, 128) important=rgb(0, 128, 0)|important cleared=rgb(128, 0, 128)|",
            Run("""
                (function () {
                  var rule = sheetOf('author').cssRules[0], p = document.getElementById('probe');
                  p.style.color = 'rgb(128, 0, 128)';
                  var inline = getComputedStyle(p).color;
                  rule.style.setProperty('color', 'rgb(0, 128, 0)', 'important');
                  var important = getComputedStyle(p).color + '|' + rule.style.getPropertyPriority('color');
                  rule.style.setProperty('color', 'rgb(0, 128, 0)');
                  return 'inline=' + inline + ' important=' + important +
                         ' cleared=' + getComputedStyle(p).color + '|' + rule.style.getPropertyPriority('color');
                })()
                """));
    }

    [Fact]
    public void CssTextReplacesEveryDeclarationOfTheRule()
    {
        // Setting cssText empties the block and parses the string into it, dropping what does not parse:
        // the first write leaves only display, the second only color, the third nothing.
        Assert.Equal(
            "before=block/rgb(255, 0, 0) display=flex/rgb(0, 0, 255)|#probe { display: flex; } " +
            "color=inline/rgb(0, 128, 0) empty=inline/rgb(0, 0, 255)|[]",
            Run("""
                (function () {
                  var rule = sheetOf('author').cssRules[0];
                  var before = read();
                  rule.style.cssText = 'display: flex';
                  var display = read() + '|' + rule.cssText;
                  rule.style.cssText = 'display: bogus; color: rgb(0, 128, 0)';
                  var color = read();
                  rule.style.cssText = '';
                  return 'before=' + before + ' display=' + display + ' color=' + color +
                         ' empty=' + read() + '|[' + rule.style.cssText + ']';
                })()
                """));
    }

    [Fact]
    public void StyleRulesInsideGroupingRulesAreWrittenThroughAtEveryDepth()
    {
        // #deep sits two grouping levels down and #sib one, beside it; #other is in a second top-level
        // group. The sibling's write comes after its ancestor was already changed by the first write, so it
        // has to find its rule in the edited ancestor rather than in the one it was read from. (@layer
        // blocks are written through the same way, but the consumed style engine does not apply a layered
        // rule at all yet, so a layer here would read inline before and after.)
        Assert.Equal(
            "before=block,block,block after=none,flex,grid media=true,true",
            Run("""
                (function () {
                  var s = sheetOf('nested');
                  var before = displayOf('deep') + ',' + displayOf('sib') + ',' + displayOf('other');
                  var deep = s.cssRules[0].cssRules[0].cssRules[0].style, sib = s.cssRules[0].cssRules[1].style;
                  deep.setProperty('display', 'none');
                  sib.setProperty('display', 'flex');
                  s.cssRules[1].cssRules[0].style.setProperty('display', 'grid');
                  var media = s.cssRules[0].cssText;
                  return 'before=' + before +
                         ' after=' + displayOf('deep') + ',' + displayOf('sib') + ',' + displayOf('other') +
                         ' media=' + (media.indexOf('display: none') >= 0) + ',' + (media.indexOf('display: flex') >= 0);
                })()
                """));
    }

    [Fact]
    public void AWriteReachesTheStyleTextTheRendererIsHanded()
    {
        // Paint and layout read the <style> text the serialized page carries, so a write that reached
        // getComputedStyle and not that text would render the rule as authored.
        var html = RunHtml("""
            (function () {
              var before = read();
              sheetOf('author').cssRules[0].style.setProperty('display', 'none');
              return 'before=' + before + ' after=' + read();
            })()
            """);

        Assert.Equal("before=block/rgb(255, 0, 0) after=none/rgb(255, 0, 0)", OutOf(html));
        var text = StyleTextOf(html, "author");
        Assert.Contains("display: none", text);
        Assert.DoesNotContain("display: block", text);
    }

    [Fact]
    public void AValueThatIsNotOneDeclarationIsRejectedRatherThanInjectingRules()
    {
        // The sheet is re-serialized from its rule model once a write lands, and that text is parsed
        // again by the style engine and the renderer. A custom property accepts almost any value, so a
        // value carrying a top-level ';' or an unmatched '}' would otherwise close the declaration or
        // the rule and start new ones. CSSOM rejects both: neither is one declaration's value. The
        // cssText write keeps the declaration that parses and drops the one that does not.
        Assert.Equal(
            "before=block/rgb(255, 0, 0) setProperty=block/rgb(255, 0, 0)|[]|2 " +
            "cssText=inline/rgb(0, 128, 0)|2|span.box",
            Run("""
                (function () {
                  var s = sheetOf('author'), rule = s.cssRules[0];
                  var before = read();
                  rule.style.setProperty('--x', 'a; display: none');
                  rule.style.setProperty('--x', 'a } #probe { display: none');
                  var setProperty = read() + '|[' + rule.style.getPropertyValue('--x') + ']|' + s.cssRules.length;
                  rule.style.cssText = 'color: rgb(0, 128, 0); --y: } #probe { display: none';
                  return 'before=' + before + ' setProperty=' + setProperty +
                         ' cssText=' + read() + '|' + s.cssRules.length + '|' + s.cssRules[1].selectorText;
                })()
                """));
    }

    [Fact]
    public void AWriteThroughTheDeclarationOfADeletedRuleDoesNotApply()
    {
        // The declaration outlives its rule and still takes writes, but the rule is no longer in the
        // sheet, so they reach nothing.
        Assert.Equal(
            "before=block/rgb(255, 0, 0) deleted=inline/rgb(0, 0, 255) written=inline/rgb(0, 0, 255) held=none",
            Run("""
                (function () {
                  var s = sheetOf('author'), st = s.cssRules[0].style;
                  var before = read();
                  s.deleteRule(0);
                  var deleted = read();
                  st.setProperty('display', 'none');
                  return 'before=' + before + ' deleted=' + deleted + ' written=' + read() +
                         ' held=' + st.getPropertyValue('display');
                })()
                """));
    }

    [Fact]
    public void AWriteThroughADeclarationReadBeforeTheSheetTextWasReplacedDoesNotApply()
    {
        // Replacing the <style>'s text replaces its rules, so a declaration read from the old rules
        // addresses a rule the sheet no longer has.
        Assert.Equal(
            "block/rgb(255, 0, 0)",
            Run("""
                (function () {
                  var st = sheetOf('author').cssRules[0].style;
                  document.getElementById('author').textContent = '#probe { display: block; color: rgb(255, 0, 0); }';
                  st.setProperty('display', 'none');
                  return read();
                })()
                """));
    }

    [Fact]
    public void RepeatedAuthoredDeclarationsCollapseToTheOneWritten()
    {
        // setProperty leaves exactly one declaration of the property, so the -webkit-box fallback the
        // author wrote ahead of flex goes too.
        Assert.Equal(
            "before=flex after=grid cssText=display: grid;",
            Run("""
                (function () {
                  var r = sheetOf('dup').cssRules[0];
                  var before = displayOf('dup');
                  r.style.setProperty('display', 'grid');
                  return 'before=' + before + ' after=' + displayOf('dup') + ' cssText=' + r.style.cssText;
                })()
                """));
    }

    [Fact]
    public void AShorthandAndItsLonghandsAreWrittenAndRemovedTogether()
    {
        // Setting margin sets all four sides; a later margin-top overrides one of them; removing
        // margin-top leaves the other three sides as margin set them; removing margin removes those.
        Assert.Equal(
            "authored=1px margin=7px top=3px/7px removedTop=0px/7px removedMargin=0px/0px",
            Run("""
                (function () {
                  var r = sheetOf('box').cssRules[0], box = document.getElementById('box');
                  function side(p) { return getComputedStyle(box)[p]; }
                  var authored = side('marginTop');
                  r.style.setProperty('margin', '7px');
                  var margin = side('marginTop');
                  r.style.setProperty('margin-top', '3px');
                  var top = side('marginTop') + '/' + side('marginLeft');
                  r.style.removeProperty('margin-top');
                  var removedTop = side('marginTop') + '/' + side('marginLeft');
                  r.style.removeProperty('margin');
                  return 'authored=' + authored + ' margin=' + margin + ' top=' + top +
                         ' removedTop=' + removedTop + ' removedMargin=' + side('marginTop') + '/' + side('marginLeft');
                })()
                """));
    }

    [Fact]
    public void ACamelCasedAttributeReadsWhatTheRuleDeclaresWhicheverDeclarationWroteIt()
    {
        // An attribute write used to leave its value behind as an ordinary property of the declaration it was
        // made through, and ordinary properties are read before the rule is. So once two declarations shared
        // one rule, or setProperty/removeProperty/cssText changed a property a named write had set, the
        // attribute kept answering the old value — and getPropertyValue did too, falling back to the same
        // copy. This used to answer crossed=flex|grid removed=none|none replaced=rgb(0, 128, 0)|rgb(0, 128, 0)|none
        // keys=true.
        Assert.Equal(
            "before=block/rgb(255, 0, 0) crossed=grid|grid removed=| replaced=||inline-block keys=false " +
            "after=inline-block/rgb(0, 0, 255)",
            Run("""
                (function () {
                  var s = sheetOf('author'), a = s.cssRules[0].style, b = s.cssRules[0].style;
                  var before = read();
                  a.display = 'flex';
                  b.display = 'grid';
                  var crossed = a.display + '|' + a.getPropertyValue('display');
                  a.display = 'none';
                  a.removeProperty('display');
                  var removed = a.display + '|' + a.getPropertyValue('display');
                  a.color = 'rgb(0, 128, 0)';
                  a.cssText = 'display: inline-block';
                  var replaced = a.color + '|' + a.getPropertyValue('color') + '|' + a.display;
                  return 'before=' + before + ' crossed=' + crossed + ' removed=' + removed +
                         ' replaced=' + replaced + ' keys=' + (Object.keys(a).indexOf('display') >= 0) +
                         ' after=' + read();
                })()
                """));
    }

    [Fact]
    public void RemovingACamelCasedNameIsANoOp()
    {
        // CSSOM lowercases the name, and margintop is no property, so nothing is removed; the camel-cased
        // spelling used to remove margin-top too, splitting the margin shorthand in the sheet. This used to
        // answer after=0px cssText=#box { margin-right: 1px; margin-bottom: 1px; margin-left: 1px; }.
        var html = RunHtml("""
            (function () {
              var r = sheetOf('box').cssRules[0], box = document.getElementById('box');
              var before = getComputedStyle(box).marginTop;
              var returned = r.style.removeProperty('marginTop');
              return 'before=' + before + ' returned=[' + returned + '] after=' + getComputedStyle(box).marginTop +
                     ' cssText=' + r.cssText;
            })()
            """);

        Assert.Equal("before=1px returned=[] after=1px cssText=#box { margin: 1px; }", OutOf(html));
        Assert.Equal("#box { margin: 1px; }", StyleTextOf(html, "box"));
    }

    [Fact]
    public void ACustomPropertyIsAddressedByItsExactNameOnly()
    {
        // A custom property has no attribute, so style['--myVar'] is an ordinary property of the object, and
        // removeProperty('--myVar') removes --myVar alone. Both used to reach --my-var as well, the name the
        // camel-to-kebab rewrite makes of --myVar. This used to answer named=1|5|5 returned=1 after=[]|[].
        Assert.Equal(
            "named=1|2|5 returned=1 after=[]|[2]",
            Run("""
                (function () {
                  var r = sheetOf('vars').cssRules[0];
                  r.style['--myVar'] = '5';
                  var named = r.style.getPropertyValue('--myVar') + '|' + r.style.getPropertyValue('--my-var') + '|' +
                              r.style['--myVar'];
                  var returned = r.style.removeProperty('--myVar');
                  return 'named=' + named + ' returned=' + returned +
                         ' after=[' + r.style.getPropertyValue('--myVar') + ']|[' + r.style.getPropertyValue('--my-var') + ']';
                })()
                """));
    }

    [Fact]
    public void APriorityOtherThanImportantOrAnImportantInsideTheValueIsIgnored()
    {
        // CSSOM setProperty returns unless the priority is "" or an ASCII case-insensitive "important", and
        // "!important" is not part of any property's value grammar, so none of the first three writes is an
        // edit. They used to write normal and then important green — the answer was
        // ignored=block/rgb(0, 128, 0)|important upper=flex/rgb(0, 128, 0)|important — and so replace the
        // author's text with the model's.
        var html = RunHtml("""
            (function () {
              var rule = sheetOf('author').cssRules[0];
              var before = read();
              rule.style.setProperty('color', 'rgb(0, 128, 0)', 'bogus');
              rule.style.setProperty('color', 'rgb(0, 128, 0) !important');
              rule.style.color = 'rgb(0, 128, 0) !important';
              var ignored = read() + '|' + rule.style.getPropertyPriority('color');
              rule.style.setProperty('display', 'flex', 'IMPORTANT');
              return 'before=' + before + ' ignored=' + ignored +
                     ' upper=' + read() + '|' + rule.style.getPropertyPriority('display');
            })()
            """);

        Assert.Equal(
            "before=block/rgb(255, 0, 0) ignored=block/rgb(255, 0, 0)| upper=flex/rgb(255, 0, 0)|important",
            OutOf(html));
        Assert.DoesNotContain("rgb(0, 128, 0)", StyleTextOf(html, "author"));
    }

    [Fact]
    public void CssFloatAndARecasedNameWriteThroughWhileANonAsciiFoldDoesNot()
    {
        // cssFloat is setProperty('float', value), so a valid value reaches the cascade and "" removes the
        // declaration. A name is lowercased as ASCII, so DISPLAY is display, while the Kelvin sign (U+212A),
        // which a Unicode lowercase folds to k, leaves "bacKground-color" a name that is no property.
        // The last write used to land: this answered kelvin=rgb(0, 128, 0).
        Assert.Equal(
            "before=none set=left|left|left cleared=none|[]|false upper=flex/rgb(255, 0, 0) kelvin=rgba(0, 0, 0, 0)",
            Run("""
                (function () {
                  var rule = sheetOf('author').cssRules[0], p = document.getElementById('probe');
                  function fl() { return getComputedStyle(p).getPropertyValue('float'); }
                  var before = fl();
                  rule.style.cssFloat = 'left';
                  var set = fl() + '|' + rule.style.cssFloat + '|' + rule.style.getPropertyValue('float');
                  rule.style.cssFloat = '';
                  var cleared = fl() + '|[' + rule.style.cssFloat + ']|' + (rule.cssText.indexOf('float') >= 0);
                  rule.style.setProperty('DISPLAY', 'flex');
                  rule.style.setProperty('bacKground-color', 'rgb(0, 128, 0)');
                  return 'before=' + before + ' set=' + set + ' cleared=' + cleared + ' upper=' + read() +
                         ' kelvin=' + getComputedStyle(p).backgroundColor;
                })()
                """));
    }

    [Fact]
    public void ALogicalShorthandIsNotThePhysicalSidesItComputesTo()
    {
        // The engine expands margin-inline into margin-left and margin-right, but those are not its longhands
        // (margin-inline-start and -end are). So removing margin-left from a rule that only says margin-inline
        // removes nothing, and removing margin-inline leaves a margin-left declared after it. These used to
        // split margin-inline into margin-right alone, and remove margin-left along with margin-inline:
        // the answer was after=0px/0px kept=#logical { margin-right: 10px; }.
        Assert.Equal(
            "before=10px/3px after=10px/3px kept=#logical { margin-inline: 10px; }",
            Run("""
                (function () {
                  var s = sheetOf('logical');
                  function left(id) { return getComputedStyle(document.getElementById(id)).marginLeft; }
                  var before = left('logical') + '/' + left('logical2');
                  s.cssRules[0].style.removeProperty('margin-left');
                  s.cssRules[1].style.removeProperty('margin-inline');
                  return 'before=' + before + ' after=' + left('logical') + '/' + left('logical2') +
                         ' kept=' + s.cssRules[0].cssText;
                })()
                """));
    }

    [Fact]
    public void AnAdoptedConstructedSheetsStyleWriteReachesTheRenderedSheet()
    {
        // getComputedStyle does not read adopted sheets yet (AdoptedStyleSheetTests has the skipped test),
        // so this reads what does: the sheet's own rules, and the adopted sheet the renderer is handed.
        var html = RunHtml("""
            (function () {
              var cs = new CSSStyleSheet();
              cs.replaceSync('#probe { outline-color: rgb(255, 0, 0); }');
              document.adoptedStyleSheets = [cs];
              var st = cs.cssRules[0].style;
              cs.insertRule('.z { color: red; }', 0);
              st.setProperty('outline-color', 'rgb(0, 128, 0)');
              return cs.cssRules[1].style.getPropertyValue('outline-color');
            })()
            """);

        Assert.Equal("rgb(0, 128, 0)", OutOf(html));
        Assert.Contains("outline-color: rgb(0, 128, 0)", html);
        Assert.DoesNotContain("outline-color: rgb(255, 0, 0)", html);
    }

    [Fact]
    public void Characterization_AStyleWriteOnARuleInsertedIntoAGroupingRuleStaysOnThatObject()
    {
        // Unchanged: a rule inserted through a grouping rule's cssRules never reaches the sheet's rule
        // model (CssRuleObjectTests.Characterization_ANestedInsertLivesOnlyOnTheRuleObjectItWasMadeThrough),
        // so neither it nor a write to its style applies. The bridge puts insertRule on the grouping rule's
        // CSSRuleList; Chromium has none there, so it throws a TypeError at m.cssRules.insertRule. Written
        // as Chromium's m.insertRule — which the bridge does not have — the script answers
        // inserted=none/rgb(255, 0, 0) written=flex/rgb(255, 0, 0) held=flex.
        Assert.Equal(
            "inserted=block/rgb(255, 0, 0) written=block/rgb(255, 0, 0) held=flex",
            Run("""
                (function () {
                  var m = sheetOf('group').cssRules[0];
                  m.cssRules.insertRule('#probe { display: none; }', 1);
                  var inserted = read();
                  m.cssRules[1].style.setProperty('display', 'flex');
                  return 'inserted=' + inserted + ' written=' + read() + ' held=' + m.cssRules[1].style.display;
                })()
                """));
    }

    [Fact]
    public void Characterization_DescriptorAndKeyframeDeclarationWritesStayOnTheObject()
    {
        // Unchanged: @font-face and @page blocks are descriptors, not properties, and a keyframe's
        // declarations feed animation sampling rather than the cascade; writes to them stay on the object
        // they were made through, and the renderer keeps the authored text. Chromium answers
        // Other|2in|0.5 on the re-read as well.
        var html = RunHtml("""
            (function () {
              var s = sheetOf('descriptors');
              var ff = s.cssRules[0].style, page = s.cssRules[1].style, key = s.cssRules[2].cssRules[0].style;
              ff.setProperty('font-family', 'Other');
              page.setProperty('margin-top', '2in');
              key.setProperty('opacity', '0.5');
              return 'held=' + ff.getPropertyValue('font-family') + '|' + page.getPropertyValue('margin-top') + '|' +
                     key.getPropertyValue('opacity') +
                     ' reread=' + s.cssRules[0].style.getPropertyValue('font-family') + '|' +
                     s.cssRules[1].style.getPropertyValue('margin-top') + '|' +
                     s.cssRules[2].cssRules[0].style.getPropertyValue('opacity');
            })()
            """);

        Assert.Equal("held=Other|2in|0.5 reread=Probe|1in|1", OutOf(html));
        Assert.Equal(
            "@font-face { font-family: Probe; src: local(Arial); } @page { margin-top: 1in; } " +
            "@keyframes spin { from { opacity: 1; } }",
            StyleTextOf(html, "descriptors"));
    }
}
