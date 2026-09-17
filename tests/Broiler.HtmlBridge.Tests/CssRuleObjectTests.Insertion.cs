namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The insertion half of <see cref="CssRuleObjectTests"/>: <c>insertRule</c> on a sheet and on a rule's
/// <c>cssRules</c> list, the text each one accepts, and the <c>cssText</c> a rule gives back — which is
/// where the bridge's old text builder was reached directly, because a nested insert handed the page's raw
/// string to it without parsing it first.
/// </summary>
public partial class CssRuleObjectTests
{
    [Fact]
    public void InsertRuleThrowsSyntaxErrorUnlessTheTextIsExactlyOneRule()
    {
        // CSSOM "parse a CSS rule": the text must be one rule with nothing but whitespace around it, and
        // an at-rule the UA does not support (or @charset, which is not a rule), or one written in a form
        // its grammar does not have ('@media screen;', a @namespace with a block), is a SyntaxError.
        // Chromium throws for every case marked SyntaxError. A rule with nested rules ('.e { .f {} }') is
        // one rule even though the consumed parser flattens it into two, and a block cut off by the end
        // of the text is still a rule. This used to answer
        //   ok:0:2 ok:0:1 ok:0:1 ok:0:2 ok:0:2 ok:0:2 ok:0:2 ok:0:2 ok:0:2 ok:0:2 ok:0:2 ok:0:2 ok:0:2 ok:0:2
        //   | ok:0:2 ok:0:2 ok:0:2 ok:0:2
        // — at sheet level text with no rule inserted nothing and returned the index, and text with more
        // than one kept the first; a nested list inserted whatever the text builder made of the string.
        // The same grammar test hides a wrong-form at-rule in sheet text too, where it used to appear.
        Assert.Equal(
            "SyntaxError:1 SyntaxError:1 SyntaxError:1 SyntaxError:1 SyntaxError:1 SyntaxError:1 SyntaxError:1 " +
            "SyntaxError:1 SyntaxError:1 SyntaxError:1 ok:0:2 ok:0:2 ok:0:2 ok:0:2 | " +
            "SyntaxError:1 SyntaxError:1 SyntaxError:1 ok:0:2",
            Run("""
                (function () {
                  function attempt(t) {
                    var s = sheet('.a { color: red; }');
                    try { var i = s.insertRule(t, 0); return 'ok:' + i + ':' + s.cssRules.length; }
                    catch (e) { return e.name + ':' + s.cssRules.length; }
                  }
                  function attemptNested(t) {
                    var m = sheet('@media screen { .a { color: red; } }').cssRules[0];
                    try { var i = m.cssRules.insertRule(t, 0); return 'ok:' + i + ':' + m.cssRules.length; }
                    catch (e) { return e.name + ':' + m.cssRules.length; }
                  }
                  var sheetLevel = ['@foo bar;', 'garbage', '', '.b {} .c {}', '.b { color: blue } junk',
                                    '@charset "utf-8";', '@mediaeval { }', '@media screen;', '@font-face;',
                                    '@namespace svg url(http://www.w3.org/2000/svg) { }',
                                    '.e { .f { color: red; } }', '@container (min-width: 1px) { .d { color: red; } }',
                                    '.u { color: blue', '@layer a;'].map(attempt);
                  var nested = ['@foo;', 'garbage', '.x {} .y {}', '  .z { color: red; }  '].map(attemptNested);
                  return sheetLevel.join(' ') + ' | ' + nested.join(' ');
                })()
                """));
    }

    [Fact]
    public void NestedInsertRuleParsesItsTextTheWayTheSheetDoes()
    {
        // A rule inserted into an @media or @supports list is parsed as sheet text is: leading whitespace
        // and comments skipped, a selector list written ', '-separated, and the declarations after a
        // nested rule kept on the rule. This used to answer
        //   ia print|.b,.c|/* note */ .d|.s,.t|5 hoisted=false:green
        // — the text builder cut the media text at a fixed offset into the untrimmed string, kept the raw
        // selector text comments and all, and dropped every declaration in the segment holding the nested
        // rule, so the same text gave a different style inserted into a list than into a sheet. The
        // three entry points (sheet text, sheet insertRule, list insertRule) now agree. Chromium agrees
        // on the first five fields (inserting through m.insertRule); for the last it answers true: too,
        // because CSS Nesting in Chromium moves the trailing declaration into a CSSNestedDeclarations child
        // on both sides, which the consumed parser cannot represent.
        Assert.Equal(
            "print|.b, .c|.d|.s, .t|5 hoisted=true:green",
            Run("""
                (function () {
                  var rules = sheet('@media screen { .a { color: red; } } @supports (display: grid) { .g { color: red; } }').cssRules;
                  var m = rules[0], sup = rules[1];
                  m.cssRules.insertRule('  @media print { .x { color: red; } }', 1);
                  m.cssRules.insertRule('.b,.c{color:blue}', 2);
                  m.cssRules.insertRule('/* note */ .d { color: green }', 3);
                  sup.cssRules.insertRule('.s,.t{color:red}', 1);
                  var n = '.n { color: red; .inner { color: blue; } background-color: green; }';
                  m.cssRules.insertRule(n, 4);
                  var top = new CSSStyleSheet();
                  top.insertRule(n, 0);
                  return m.cssRules[1].media + '|' + m.cssRules[2].selectorText + '|' + m.cssRules[3].selectorText + '|' +
                         sup.cssRules[1].selectorText + '|' + m.cssRules.length +
                         ' hoisted=' + (m.cssRules[4].style.backgroundColor === top.cssRules[0].style.backgroundColor) +
                         ':' + top.cssRules[0].style.backgroundColor;
                })()
                """));
    }

    [Fact]
    public void KeyframesRuleListsHoldOnlyKeyframeRules()
    {
        // Chromium's CSSKeyframesRule holds keyframes and nothing else: an @media inside @keyframes is
        // dropped from sheet text, and text that is not a keyframe is rejected. This used to answer
        //   count=3 keys=from|@media screen|50% inserted=75%,80% type=8 garbage=ok:5 media=ok:6
        // — every child became a CSSKeyframeRule, the @media one with keyText '@media screen', and a
        // nested insert kept the raw key text and accepted anything. The inserted key is now the parsed
        // key list. Chromium rejects invalid text through appendRule silently rather than with a
        // SyntaxError; the list-level insertRule the bridge offers has no Chromium counterpart.
        Assert.Equal(
            "count=2 keys=from|50% inserted=75%, 80% type=8 garbage=SyntaxError:3 media=SyntaxError:3",
            Run("""
                (function () {
                  var k = sheet('@keyframes spin { from { color: red; } @media screen { } 50% { color: blue; } }').cssRules[0];
                  var list = k.cssRules, keys = [];
                  for (var i = 0; i < list.length; i++) keys.push(list[i].keyText);
                  var count = list.length;
                  list.insertRule('75%,80% { color: green }', list.length);
                  var last = list[list.length - 1];
                  function attempt(t) {
                    try { list.insertRule(t, 0); return 'ok:' + list.length; }
                    catch (e) { return e.name + ':' + list.length; }
                  }
                  return 'count=' + count + ' keys=' + keys.join('|') + ' inserted=' + last.keyText +
                         ' type=' + last.type + ' garbage=' + attempt('garbage') + ' media=' + attempt('@media screen { }');
                })()
                """));
    }

    [Fact]
    public void CssTextRoundTripsThroughInsertRuleAtBothLevels()
    {
        // A rule's cssText, inserted again into a sheet and into an @media list, gives back the same
        // cssText. This held before only because both sides lost the same things: @container came back
        // as '@container (min-width: 1px) {  }', a style rule with its own prelude as the selector and
        // its child dropped. Chromium's cssText formats differ for grouping rules (multi-line, url("...")
        // for @namespace), but the sheet-level round trip holds there too. The nested half has no full
        // Chromium counterpart: CSSRuleList has no insertRule, and the grouping rule's own insertRule
        // rejects @namespace (and @import) with a HierarchyRequestError, which the bridge's list accepts.
        Assert.Equal(
            "same same same same same same same same same same same same " +
            "container=@container (min-width: 1px) { .a { color: red; } }",
            Run("""
                (function () {
                  var texts = ['.a { color: red; }', '@media screen { .a { color: red; } }',
                               '@supports (display: grid) { .a { color: red; } }',
                               '@keyframes spin { from { color: red; } to { color: blue; } }',
                               '@font-face { font-family: Probe; }', '@page :first { margin-top: 1in; }',
                               '@layer base { .a { color: red; } }', '@layer a, b;',
                               '@property --x { syntax: "*"; inherits: false; }',
                               '@counter-style thumbs { system: cyclic; symbols: x; }',
                               '@namespace svg "http://www.w3.org/2000/svg";',
                               '@container (min-width: 1px) { .a { color: red; } }'];
                  var out = [], container = '';
                  for (var i = 0; i < texts.length; i++) {
                    var s = sheet(texts[i]), a = s.cssRules[0].cssText;
                    s.insertRule(a, 1);
                    var b = s.cssRules[1].cssText;
                    var mm = sheet('@media screen { }').cssRules[0];
                    mm.cssRules.insertRule(a, 0);
                    var c = mm.cssRules[0].cssText;
                    out.push(a === b && a === c ? 'same' : 'diff[' + a + '|' + b + '|' + c + ']');
                    container = a;
                  }
                  return out.join(' ') + ' container=' + container;
                })()
                """));
    }

    [Fact]
    public void Characterization_KeyframeKeysAreNotNormalizedAndTheKeyframesRuleHasNoAppendFindOrDeleteRule()
    {
        // Unchanged by the builder swap. Chromium answers keys=0%|100% (a keyframe selector serializes
        // from and to as percentages) and function,function,function: CSSKeyframesRule.appendRule,
        // findRule and deleteRule are how a page edits keyframes there. Both are separate work.
        Assert.Equal(
            "keys=from|to api=undefined,undefined,undefined",
            Run("""
                (function () {
                  var k = sheet('@keyframes spin { from { color: red; } to { color: blue; } }').cssRules[0];
                  return 'keys=' + k.cssRules[0].keyText + '|' + k.cssRules[1].keyText +
                         ' api=' + typeof k.appendRule + ',' + typeof k.findRule + ',' + typeof k.deleteRule;
                })()
                """));
    }

    [Fact]
    public void Characterization_ANestedInsertLivesOnlyOnTheRuleObjectItWasMadeThrough()
    {
        // Unchanged by the builder swap. Rule objects are rebuilt each time sheet.cssRules is read, and a
        // nested list insert changes only that object's list, not the parsed model the sheet rebuilds
        // from (so it is never rendered either). Chromium answers held=2 reread=2 identity=true: a rule
        // object is the same object on every read, and CSSGroupingRule.insertRule changes the rule.
        Assert.Equal(
            "held=2 reread=1 identity=false",
            Run("""
                (function () {
                  var s = sheet('@media screen { .a { color: red; } }');
                  var m = s.cssRules[0];
                  m.cssRules.insertRule('.b { color: blue; }', 1);
                  return 'held=' + m.cssRules.length + ' reread=' + s.cssRules[0].cssRules.length +
                         ' identity=' + (s.cssRules[0] === s.cssRules[0]);
                })()
                """));
    }
}
