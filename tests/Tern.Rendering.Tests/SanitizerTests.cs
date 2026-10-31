using System.Text.RegularExpressions;
using Tern.Rendering.Html;

namespace Tern.Rendering.Tests;

/// <summary>
/// Independent re-tokeniser used to judge sanitiser output. It deliberately shares no code with the library:
/// any tag, attribute or URL that is not on the allow-list below fails the assertion.
/// </summary>
internal static partial class StrictHtmlChecker
{
    private static readonly HashSet<string> Tags = new(StringComparer.Ordinal)
    {
        "b", "i", "em", "strong", "u", "s", "del", "ins", "mark", "sub", "sup", "kbd", "code", "pre", "br", "hr",
        "p", "div", "span", "details", "summary", "blockquote", "ul", "ol", "li", "table", "thead", "tbody", "tfoot",
        "tr", "td", "th", "caption", "colgroup", "col", "h1", "h2", "h3", "h4", "h5", "h6", "a", "img", "figure",
        "figcaption", "abbr", "small", "cite", "q", "samp", "var", "dl", "dt", "dd",
    };

    private static readonly HashSet<string> VoidTags = new(StringComparer.Ordinal) { "br", "hr", "img", "col" };

    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "href", "src", "title", "alt", "width", "height", "align", "open", "colspan", "rowspan", "start", "span", "rel", "id", "name",
    };

    [GeneratedRegex(@"<(/?)([^\s>/]+)((?:[^>""']|""[^""]*""|'[^']*')*)>", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"([^\s=""'<>/]+)\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedAttribute();

    [GeneratedRegex(@"\s([^\s=""'<>/]+)(?=\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex BareAttribute();

    public static void AssertSafe(string html, string because)
    {
        string Fail(string why) => $"{why}\nInput: {because}\nOutput: {html}";

        Assert.False(html.Contains('\0', StringComparison.Ordinal), Fail("NUL in output"));

        var stack = new Stack<string>();
        var position = 0;
        foreach (Match tag in TagPattern().Matches(html))
        {
            var between = html[position..tag.Index];
            Assert.False(between.Contains('<', StringComparison.Ordinal), Fail("stray '<' in text: " + between));
            position = tag.Index + tag.Length;

            var closing = tag.Groups[1].Value.Length > 0;
            var name = tag.Groups[2].Value;
            Assert.True(Tags.Contains(name), Fail("tag not on allow-list: " + name));
            var attributeText = tag.Groups[3].Value;

            if (closing)
            {
                Assert.True(attributeText.Trim().Length == 0, Fail("closing tag with attributes"));
                Assert.False(VoidTags.Contains(name), Fail("closing void tag " + name));
                Assert.True(stack.Count > 0 && stack.Pop() == name, Fail("unbalanced closing tag " + name));
                continue;
            }

            CheckAttributes(name, attributeText, Fail);
            if (!VoidTags.Contains(name) && !attributeText.TrimEnd().EndsWith('/'))
            {
                stack.Push(name);
            }
        }

        var tail = html[position..];
        Assert.False(tail.Contains('<', StringComparison.Ordinal), Fail("stray '<' at end: " + tail));
        Assert.True(stack.Count == 0, Fail("unclosed tags: " + string.Join(",", stack)));
    }

    private static void CheckAttributes(string tag, string attributeText, Func<string, string> fail)
    {
        var remaining = attributeText;
        foreach (Match attribute in QuotedAttribute().Matches(attributeText))
        {
            var name = attribute.Groups[1].Value;
            var value = System.Net.WebUtility.HtmlDecode(attribute.Groups[2].Value);
            Assert.True(Attributes.Contains(name), fail($"attribute not allowed: {name} on {tag}"));
            Assert.False(name.StartsWith("on", StringComparison.Ordinal), fail("event handler " + name));
            if (name is "href" or "src")
            {
                CheckUrl(name, value, fail);
            }
            else if (name is "id" or "name")
            {
                Assert.True(name == "id" || tag == "a", fail("legacy name is allowed only on anchors"));
                Assert.InRange(value.Length, 1, 256);
                foreach (var character in value)
                    Assert.False(char.IsControl(character) || char.IsWhiteSpace(character)
                        || character is '<' or '>' or '"' or '\'', fail("invalid inert identifier"));
            }

            remaining = remaining.Replace(attribute.Value, " ", StringComparison.Ordinal);
        }

        foreach (Match bare in BareAttribute().Matches(remaining))
        {
            Assert.True(Attributes.Contains(bare.Groups[1].Value), fail("attribute not allowed: " + bare.Groups[1].Value));
            Assert.False(bare.Groups[1].Value is "id" or "name", fail("an inert identifier requires a quoted bounded value"));
        }

        Assert.False(remaining.Contains('"', StringComparison.Ordinal) || remaining.Contains('\'', StringComparison.Ordinal), fail("unparsed quote in tag: " + attributeText));
    }

    private static void CheckUrl(string attribute, string value, Func<string, string> fail)
    {
        foreach (var c in value)
        {
            Assert.False(char.IsControl(c) || char.IsWhiteSpace(c) && c != ' ', fail("control character in URL"));
        }

        var trimmed = value.Trim();
        Assert.False(trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("\\\\", StringComparison.Ordinal), fail("network-path URL"));
        var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        var firstSlash = trimmed.IndexOfAny(['/', '?', '#']);
        if (colon >= 0 && (firstSlash < 0 || colon < firstSlash))
        {
            var scheme = trimmed[..colon].ToLowerInvariant();
            var allowed = attribute == "href" && scheme is "http" or "https" or "mailto";
            Assert.True(allowed, fail($"scheme '{scheme}' not allowed in {attribute}"));
        }
    }
}

public sealed class SanitizerTests
{
    private readonly HtmlSanitizer _sanitizer = new();

    public static TheoryData<string> Attacks()
    {
        var vectors = new[]
        {
            "<script>alert(1)</script>",
            "<SCRIPT>alert(1)</SCRIPT>",
            "<ScRiPt>alert(1)</sCrIpT>",
            "<script src=//evil.example/x.js></script>",
            "<script/src=data:,alert(1)>",
            "<script\n>alert(1)</script\n>",
            "<script>alert(1)",
            "<scr<script>ipt>alert(1)</scr</script>ipt>",
            "<<script>alert(1);//<</script>",
            "<script\u0000>alert(1)</script>",
            "<scri\u0000pt>alert(1)</scri\u0000pt>",
            "<img src=x onerror=alert(1)>",
            "<IMG SRC=x ONERROR=alert(1)>",
            "<img src=\"x\" onerror=\"alert(1)\">",
            "<img src='x' onerror='alert(1)'>",
            "<img/src=x/onerror=alert(1)>",
            "<img src=x onerror=alert(1)//",
            "<img src=x\u0000onerror=alert(1)>",
            "<img src=\"x\"onerror=\"alert(1)\">",
            "<img src=x onerror\n=\n alert(1)>",
            "<img src=`x` onerror=alert(1)>",
            "<img srcset=\"x 1x, javascript:alert(1) 2x\">",
            "<img src=\"javascript:alert(1)\">",
            "<img dynsrc=\"javascript:alert(1)\">",
            "<img lowsrc=\"javascript:alert(1)\">",
            "<img src=\"http://evil.example/track.png\">",
            "<img src=\"data:image/svg+xml;base64,PHN2ZyBvbmxvYWQ9YWxlcnQoMSk+\">",
            "<a href=\"javascript:alert(1)\">x</a>",
            "<a href=\"JAVASCRIPT:alert(1)\">x</a>",
            "<a href=\"  javascript:alert(1)\">x</a>",
            "<a href=\"java\tscript:alert(1)\">x</a>",
            "<a href=\"java\nscript:alert(1)\">x</a>",
            "<a href=\"java\rscript:alert(1)\">x</a>",
            "<a href=\"java&#09;script:alert(1)\">x</a>",
            "<a href=\"java&#x0A;script:alert(1)\">x</a>",
            "<a href=\"java&Tab;script:alert(1)\">x</a>",
            "<a href=\"java&NewLine;script:alert(1)\">x</a>",
            "<a href=\"&#106;&#97;&#118;&#97;&#115;&#99;&#114;&#105;&#112;&#116;&#58;alert(1)\">x</a>",
            "<a href=\"&#x6A;&#x61;&#x76;&#x61;&#x73;&#x63;&#x72;&#x69;&#x70;&#x74;&#x3A;alert(1)\">x</a>",
            "<a href=\"&#0000106&#0000097&#0000118&#0000097&#0000115&#0000099&#0000114&#0000105&#0000112&#0000116&#0000058alert(1)\">x</a>",
            "<a href=\"javascript&colon;alert(1)\">x</a>",
            "<a href=\"javascript&#58;alert(1)\">x</a>",
            "<a href=\"javascript&#x3a;alert(1)\">x</a>",
            "<a href=\"\u0001javascript:alert(1)\">x</a>",
            "<a href=\"\u00a0javascript:alert(1)\">x</a>",
            "<a href=\"\u2028javascript:alert(1)\">x</a>",
            "<a href=\"jav\u0000ascript:alert(1)\">x</a>",
            "<a href=javascript:alert(1)>x</a>",
            "<a href='javascript:alert(1)'>x</a>",
            "<a href=\"vbscript:msgbox(1)\">x</a>",
            "<a href=\"livescript:alert(1)\">x</a>",
            "<a href=\"data:text/html,<script>alert(1)</script>\">x</a>",
            "<a href=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\">x</a>",
            "<a href=\"file:///C:/Windows/win.ini\">x</a>",
            "<a href=\"//evil.example/x\">x</a>",
            "<a href=\"\\\\evil\\share\">x</a>",
            "<a href=\"ms-msdt:/id PCWDiagnostic\">x</a>",
            "<a href=\"x\" onclick=\"alert(1)\">x</a>",
            "<a href=\"x\" onmouseover=alert(1)>x</a>",
            "<a href=\"x\" ONFOCUS=alert(1) autofocus>x</a>",
            "<a href=x target=_blank>x</a>",
            "<a href=\"x\" style=\"background:url(javascript:alert(1))\">x</a>",
            "<a href=\"x\" xlink:href=\"javascript:alert(1)\">x</a>",
            "<a href=\"x\" ping=\"https://evil.example\">x</a>",
            "<a href=\"x\" download=\"a.exe\">x</a>",
            "<a href=\"x\" formaction=\"javascript:alert(1)\">x</a>",
            "<a\nhref\n=\n\"javascript:alert(1)\">x</a>",
            "<a href=\"javascript:alert(1)\"\nonclick=alert(2)>x</a>",
            "<svg onload=alert(1)>",
            "<svg/onload=alert(1)>",
            "<svg><script>alert(1)</script></svg>",
            "<svg><animate onbegin=alert(1) attributeName=x dur=1s>",
            "<svg><a xlink:href=\"javascript:alert(1)\"><text>x</text></a></svg>",
            "<svg><foreignObject><iframe srcdoc=\"<script>alert(1)</script>\"></foreignObject></svg>",
            "<svg><use href=\"data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=#x\"/></svg>",
            "<math><mtext><table><mglyph><style><img src=x onerror=alert(1)>",
            "<math href=\"javascript:alert(1)\">x</math>",
            "<iframe src=\"javascript:alert(1)\"></iframe>",
            "<iframe srcdoc=\"<script>alert(1)</script>\"></iframe>",
            "<iframe srcdoc=\"&lt;script&gt;alert(1)&lt;/script&gt;\">",
            "<iframe src=//evil.example/>",
            "<frame src=\"javascript:alert(1)\">",
            "<frameset onload=alert(1)>",
            "<object data=\"javascript:alert(1)\"></object>",
            "<object data=\"data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==\"></object>",
            "<embed src=\"javascript:alert(1)\">",
            "<embed src=x onerror=alert(1)>",
            "<applet code=x.class></applet>",
            "<form action=\"javascript:alert(1)\"><input type=submit></form>",
            "<form><button formaction=\"javascript:alert(1)\">x</button></form>",
            "<input autofocus onfocus=alert(1)>",
            "<input type=image src=x onerror=alert(1)>",
            "<button onclick=alert(1)>x</button>",
            "<select onfocus=alert(1) autofocus><option>x</option></select>",
            "<textarea autofocus onfocus=alert(1)></textarea>",
            "<textarea><script>alert(1)</script></textarea>",
            "<keygen autofocus onfocus=alert(1)>",
            "<isindex type=image src=1 onerror=alert(1)>",
            "<video src=x onerror=alert(1)></video>",
            "<video><source onerror=alert(1)></video>",
            "<audio src=x onerror=alert(1)></audio>",
            "<audio controls ontimeupdate=alert(1)><source src=x></audio>",
            "<body onload=alert(1)>",
            "<body background=\"javascript:alert(1)\">",
            "<html onmouseover=alert(1)>",
            "<head><title>x</title></head>",
            "<style>@import 'javascript:alert(1)';</style>",
            "<style>*{background:url(javascript:alert(1))}</style>",
            "<style>body{behavior:url(x.htc)}</style>",
            "<style>x{width:expression(alert(1))}</style>",
            "<div style=\"width:expression(alert(1))\">x</div>",
            "<div style=\"background:url(javascript:alert(1))\">x</div>",
            "<p style=\"position:fixed;top:0;left:0;width:100%;height:100%\">overlay</p>",
            "<span style=\"-moz-binding:url(x)\">x</span>",
            "<link rel=stylesheet href=\"javascript:alert(1)\">",
            "<link rel=stylesheet href=\"//evil.example/x.css\">",
            "<meta http-equiv=\"refresh\" content=\"0;url=javascript:alert(1)\">",
            "<meta http-equiv=\"refresh\" content=\"0;url=http://evil.example\">",
            "<meta charset=\"utf-7\">",
            "<base href=\"javascript:alert(1)//\">",
            "<base href=\"//evil.example/\"><a href=x>x</a>",
            "<template><script>alert(1)</script></template>",
            "<noscript><p title=\"</noscript><img src=x onerror=alert(1)>\">",
            "<noembed><img src=x onerror=alert(1)></noembed>",
            "<noframes><img src=x onerror=alert(1)></noframes>",
            "<xmp><img src=x onerror=alert(1)></xmp>",
            "<plaintext><img src=x onerror=alert(1)>",
            "<listing><img src=x onerror=alert(1)></listing>",
            "<marquee onstart=alert(1)>x</marquee>",
            "<details open ontoggle=alert(1)>x</details>",
            "<details ontoggle=alert(1) open><summary>s</summary></details>",
            "<div onpointerover=alert(1)>x</div>",
            "<div onanimationstart=alert(1) style=\"animation-name:x\">x</div>",
            "<div contenteditable onfocus=alert(1) autofocus tabindex=0>x</div>",
            "<div id=\"x\" class=\"y\" data-x=\"y\" aria-label=\"z\">x</div>",
            "<table background=\"javascript:alert(1)\"><tr><td>x</td></tr></table>",
            "<td background=\"javascript:alert(1)\">x</td>",
            "<!--<img src=\"--><img src=x onerror=alert(1)//\">",
            "<!-- --><script>alert(1)</script><!-- -->",
            "<!--[if IE]><script>alert(1)</script><![endif]-->",
            "<!--[if gte IE 4]><img src=x onerror=alert(1)><![endif]-->",
            "<![if !IE]><script>alert(1)</script><![endif]>",
            "<![CDATA[<script>alert(1)</script>]]>",
            "<![CDATA[]]><script>alert(1)</script>",
            "<![CDATA[ ]]><img src=x onerror=alert(1)>",
            "<!--->< script>alert(1)</script>-->",
            "<!----><script>alert(1)</script>",
            "<!--\u0000--><script>alert(1)</script>",
            "<!DOCTYPE html><script>alert(1)</script>",
            "<?xml version=\"1.0\"?><script>alert(1)</script>",
            "<?php echo 1; ?>",
            "<% alert(1) %>",
            "<script>/*</script>*/alert(1)</script>",
            "<b><script>alert(1)</script></b>",
            "<p><img src=x onerror=alert(1)></p>",
            "<p title=\"\"><script>alert(1)</script>\">x</p>",
            "<p title='\"><script>alert(1)</script>'>x</p>",
            "<p title=\"&quot;><script>alert(1)</script>\">x</p>",
            "<p title=\"&#34;&gt;&lt;script&gt;alert(1)&lt;/script&gt;\">x</p>",
            "<img alt=\"\" src=\"x\" title=\"><script>alert(1)</script>\">",
            "<img alt=\"x\"onerror=\"alert(1)\">",
            "<img alt=x`onerror=alert(1)>",
            "<a href=\"x\"title=\"y\"onclick=\"alert(1)\">",
            "<a href=\"x\" title=\"`onmouseover=alert(1)\">x</a>",
            "<a title=\"x\" href=\"javascript:alert(1)\" title=\"y\">x</a>",
            "<a href=\"x\" href=\"javascript:alert(1)\">x</a>",
            "<a href=\"javascript:alert(1)\" href=\"x\">x</a>",
            "<a/href=\"javascript:alert(1)\">x</a>",
            "<a href=\"x\"/onclick=alert(1)>x</a>",
            "<a\u000bhref=\"javascript:alert(1)\">x</a>",
            "<a\u000chref=\"javascript:alert(1)\">x</a>",
            "<a href=\"x\" \u0000onclick=alert(1)>x</a>",
            "< script >alert(1)</ script >",
            "<script >alert(1)</script >",
            "<script type=\"text/javascript\">alert(1)</script>",
            "<script language=\"vbscript\">msgbox 1</script>",
            "<script type=\"module\">import 'x'</script>",
            "</script><script>alert(1)</script>",
            "</p></div></details><script>alert(1)</script>",
            "<b><i><u><p><div><span><script>alert(1)</script>",
            "<b <script>alert(1)</script>>x</b>",
            "<b title=\"<script>alert(1)</script>\">x</b>",
            "<b title=<script>alert(1)</script>>x</b>",
            "<b\"><script>alert(1)</script>",
            "<b/<script>alert(1)</script>",
            "<b onclick",
            "<b onclick=",
            "<img src=x onerror=",
            "<img src=\"x\" onerror=\"alert(1)",
            "<a href=\"javascript:alert(1)",
            "<a href=\"x\" title=\"unterminated><script>alert(1)</script>",
            "<",
            "<>",
            "</>",
            "<<<<<<",
            "<<img src=x onerror=alert(1)>",
            "<img src=x onerror=alert(1)<",
            "<\u0000script>alert(1)</script>",
            "<%00script>alert(1)</script>",
            "<script&#x20;>alert(1)</script>",
            "&lt;script&gt;alert(1)&lt;/script&gt;",
            "&#60;script&#62;alert(1)&#60;/script&#62;",
            "&amp;lt;script&amp;gt;alert(1)&amp;lt;/script&amp;gt;",
            "<p>&lt;img src=x onerror=alert(1)&gt;</p>",
            "<p>&#x3C;img src=x onerror=alert(1)&#x3E;</p>",
            "<p>&lt</p><script>alert(1)</script>",
            "<code>&lt;script&gt;alert(1)&lt;/script&gt;</code>",
            "<pre><script>alert(1)</script></pre>",
            "<pre>\n<img src=x onerror=alert(1)>\n</pre>",
            "<code><img src=x onerror=alert(1)></code>",
            "<table><script>alert(1)</script><tr><td>x</td></tr></table>",
            "<table><tr><td><script>alert(1)</script></td></tr></table>",
            "<table><style><img src=x onerror=alert(1)></style></table>",
            "<select><template><script>alert(1)</script></template></select>",
            "<a href=\"x\"><a href=\"javascript:alert(1)\">x</a></a>",
            "<p><p><p><p><script>alert(1)</script>",
            "<li><li><li><script>alert(1)</script>",
            "<td><td><td><script>alert(1)</script>",
            "<form><form><input onfocus=alert(1) autofocus>",
            "<math><annotation-xml encoding=\"text/html\"><script>alert(1)</script></annotation-xml></math>",
            "<svg><style><img src=x onerror=alert(1)></style></svg>",
            "<svg></p><style><a id=\"</style><img src=1 onerror=alert(1)>\">",
            "<math><mi//xlink:href=\"data:x,<script>alert(1)</script>\">",
            "<style><style/><img src=x onerror=alert(1)>",
            "<title><img src=x onerror=alert(1)></title>",
            "<iframe><img src=x onerror=alert(1)></iframe>",
            "<object><param name=x value=\"javascript:alert(1)\"></object>",
            "<img src=\"x\" alt=\"\u2028\u2029\">",
            "<a href=\"x\u0000\" onclick=alert(1)>x</a>",
            "\u0000<script>alert(1)</script>",
            "<script>alert(1)</script>\u0000",
            "<img src=x onerror=\"&#97;&#108;&#101;&#114;&#116;(1)\">",
            "<img src=x onerror=&#97;lert(1)>",
            "<img src=\"&#x6a;avascript:alert(1)\">",
            "<a href=\"&#x6a;avascript:alert(1)\">x</a>",
            "<a href=\"&#x6a&#x61;vascript:alert(1)\">x</a>",
            "<a href=\"&#106avascript:alert(1)\">x</a>",
            "<a href=\"j&#97;vascript:alert(1)\">x</a>",
            "<a href=\"javascript&#58alert(1)\">x</a>",
            "<a href=\"javascript&#x3A;alert(1)\">x</a>",
            "<a href=\"&Tab;javascript:alert(1)\">x</a>",
            "<a href=\"&#x20;javascript:alert(1)\">x</a>",
            "<a href=\"&#1;javascript:alert(1)\">x</a>",
            "<a href=\"&#x00;javascript:alert(1)\">x</a>",
            "<a href=\"javascript&#00;:alert(1)\">x</a>",
            "<a href=\"data&colon;text/html,x\">x</a>",
            "<a href=\"data&#58;text/html,x\">x</a>",
            "<a href=\"%6A%61%76%61%73%63%72%69%70%74:alert(1)\">x</a>",
        };
        var data = new TheoryData<string>();
        foreach (var vector in vectors)
        {
            data.Add(vector);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Attacks))]
    public void Attack_vectors_never_produce_executable_output(string input)
    {
        var output = _sanitizer.Sanitize(input);

        StrictHtmlChecker.AssertSafe(output, input);
    }

    [Theory]
    [MemberData(nameof(Attacks))]
    public void Sanitising_is_idempotent_and_serialisation_round_trips(string input)
    {
        var once = _sanitizer.Sanitize(input);
        var twice = _sanitizer.Sanitize(once);

        StrictHtmlChecker.AssertSafe(twice, once);
        Assert.Equal(once, HtmlSanitizer.Serialize(_sanitizer.Parse(input)));
        Assert.Equal(twice, _sanitizer.Sanitize(twice));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>after", "alert(1)")]
    [InlineData("<style>body{}</style>after", "body")]
    [InlineData("<iframe src=x>inner</iframe>after", "inner")]
    [InlineData("<object>inner</object>after", "inner")]
    [InlineData("<svg><text>inner</text></svg>after", "inner")]
    public void Dangerous_elements_lose_their_content_but_following_text_survives(string input, string dropped)
    {
        var output = _sanitizer.Sanitize(input);

        Assert.DoesNotContain(dropped, output, StringComparison.Ordinal);
        Assert.Contains("after", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_harmless_tags_keep_their_text()
    {
        var output = _sanitizer.Sanitize("<blink>hello</blink> <center>world</center> <font color=red>!</font>");

        Assert.Equal("hello world !", output);
    }

    [Fact]
    public void Event_handlers_and_styles_are_removed_but_the_element_stays()
    {
        var output = _sanitizer.Sanitize("<p onclick=\"x()\" style=\"color:red\" title=\"Hi\" class=\"c\" id=\"i\">text</p>");

        Assert.Equal("<p title=\"Hi\" id=\"i\">text</p>", output);
        Assert.DoesNotContain("onclick", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("style=", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("class=", output, StringComparison.OrdinalIgnoreCase);
        StrictHtmlChecker.AssertSafe(output, "safe id does not authorize event handlers or CSS");
    }

    [Fact]
    public void Independent_checker_accepts_only_bounded_inert_identifiers()
    {
        StrictHtmlChecker.AssertSafe("<a id=\"toc\" name=\"café\"></a><div id=\"section-1\">Text</div>", "valid inert targets");
        foreach (var invalid in new[]
        {
            "<a id=\"\"></a>", "<a id=\"two words\"></a>", "<a id=\"line&#10;break\"></a>",
            "<a id=\"&lt;script&gt;\"></a>", "<span name=\"legacy\">Text</span>", "<a id></a>",
            "<a id=\"" + new string('x', 257) + "\"></a>", "<a id=\"good\" onclick=\"x()\"></a>",
        })
            Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => StrictHtmlChecker.AssertSafe(invalid, "checker rejection regression"));
    }

    [Theory]
    [InlineData("<b>bold</b>")]
    [InlineData("<i>it</i> <em>em</em> <strong>st</strong>")]
    [InlineData("<kbd>Ctrl</kbd>+<kbd>C</kbd>")]
    [InlineData("H<sub>2</sub>O and x<sup>2</sup>")]
    [InlineData("<mark>m</mark> <u>u</u> <s>s</s> <del>d</del> <ins>i</ins>")]
    [InlineData("a<br>b")]
    [InlineData("<hr>")]
    [InlineData("<details><summary>More</summary><p>Body</p></details>")]
    [InlineData("<details open=\"\"><summary>More</summary>Body</details>")]
    [InlineData("<ul><li>one</li><li>two<ul><li>nested</li></ul></li></ul>")]
    [InlineData("<ol start=\"3\"><li>x</li></ol>")]
    [InlineData("<table><thead><tr><th colspan=\"2\">H</th></tr></thead><tbody><tr><td rowspan=\"2\" align=\"right\">a</td><td>b</td></tr></tbody></table>")]
    [InlineData("<blockquote><p>q</p></blockquote>")]
    [InlineData("<h1>t</h1><h2>t</h2><h3>t</h3><h4>t</h4><h5>t</h5><h6>t</h6>")]
    [InlineData("<pre><code>a &lt; b</code></pre>")]
    [InlineData("<abbr title=\"HyperText\">HTML</abbr>")]
    [InlineData("<figure><img src=\"a.png\" alt=\"A\" width=\"10\" height=\"20\"><figcaption>cap</figcaption></figure>")]
    [InlineData("<div align=\"center\"><span>x</span></div>")]
    [InlineData("<p>1 &lt; 2 &amp; 3 &gt; 2</p>")]
    public void Benign_html_survives_unchanged(string html)
    {
        var output = _sanitizer.Sanitize(html);

        Assert.Equal(html, output);
        StrictHtmlChecker.AssertSafe(output, html);
    }

    [Fact]
    public void Links_keep_safe_urls_and_gain_a_rel_attribute()
    {
        var output = _sanitizer.Sanitize("<a href=\"https://example.com/x?a=1&amp;b=2\" title=\"T\">x</a>");

        Assert.Contains("href=\"https://example.com/x?a=1&amp;b=2\"", output, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer\"", output, StringComparison.Ordinal);
        Assert.Contains("title=\"T\"", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<a href=\"mailto:a@b.c\">m</a>", "mailto:a@b.c")]
    [InlineData("<a href=\"#section\">s</a>", "#section")]
    [InlineData("<a href=\"docs/other.md\">s</a>", "docs/other.md")]
    [InlineData("<a href=\"../up.md#x\">s</a>", "../up.md#x")]
    public void Mail_fragment_and_relative_links_survive(string html, string href)
    {
        Assert.Contains($"href=\"{href}\"", _sanitizer.Sanitize(html), StringComparison.Ordinal);
    }

    [Fact]
    public void Remote_images_are_removed_and_local_images_kept()
    {
        Assert.DoesNotContain("src", _sanitizer.Sanitize("<img src=\"https://evil.example/x.png\" alt=\"t\">"), StringComparison.Ordinal);
        Assert.Contains("src=\"images/a.png\"", _sanitizer.Sanitize("<img src=\"images/a.png\" alt=\"t\">"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_url_policy_can_veto_or_rewrite_urls()
    {
        var kinds = new List<HtmlUrlKind>();
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            UrlPolicy = (kind, url) =>
            {
                kinds.Add(kind);
                return url.Contains("veto", StringComparison.Ordinal) ? null : "safe/" + url;
            },
        });

        var output = sanitizer.Sanitize("<a href=\"veto.md\">a</a><a href=\"ok.md\">b</a><img src=\"pic.png\" alt=\"i\"><img src=\"veto.png\">");

        Assert.DoesNotContain("veto", output, StringComparison.Ordinal);
        Assert.Contains("href=\"safe/ok.md\"", output, StringComparison.Ordinal);
        Assert.Contains("src=\"safe/pic.png\"", output, StringComparison.Ordinal);
        Assert.Contains(HtmlUrlKind.Link, kinds);
        Assert.Contains(HtmlUrlKind.Image, kinds);
    }

    [Fact]
    public void A_policy_cannot_reintroduce_a_dangerous_scheme()
    {
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions { UrlPolicy = (_, _) => "javascript:alert(1)" });

        var output = sanitizer.Sanitize("<a href=\"x.md\">a</a>");

        StrictHtmlChecker.AssertSafe(output, "policy returned javascript:");
    }

    [Fact]
    public void Output_is_balanced_for_unclosed_and_misnested_input()
    {
        Assert.Equal("<b>x</b>", _sanitizer.Sanitize("<b>x"));
        Assert.Equal("<b><i>x</i></b>", _sanitizer.Sanitize("<b><i>x</b>"));
        Assert.Equal("x", _sanitizer.Sanitize("x</b></i>"));
        Assert.Equal("<ul><li>a</li><li>b</li></ul>", _sanitizer.Sanitize("<ul><li>a<li>b"));
        Assert.Equal("<p>a</p><p>b</p>", _sanitizer.Sanitize("<p>a<p>b"));
    }

    [Fact]
    public void Void_elements_are_never_left_open()
    {
        var output = _sanitizer.Sanitize("a<br>b<br/>c</br>d<hr/>e<img src=\"a.png\" alt=\"x\"/>");

        StrictHtmlChecker.AssertSafe(output, "void elements");
        Assert.Contains("a<br>b<br>c", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_escaped_so_it_cannot_form_markup()
    {
        var output = _sanitizer.Sanitize("a < b > c & d \" e");

        Assert.DoesNotContain('<', output);
        StrictHtmlChecker.AssertSafe(output, "text");
        Assert.Contains("&lt;", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Deep_nesting_is_capped_and_still_balanced()
    {
        var input = string.Concat(Enumerable.Repeat("<div>", 5000)) + "core" + string.Concat(Enumerable.Repeat("</div>", 5000));

        var output = _sanitizer.Sanitize(input);

        StrictHtmlChecker.AssertSafe(output, "deep nesting");
        Assert.Contains("core", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Input_over_the_limit_is_handled_without_throwing()
    {
        var sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions { MaxInputLength = 1000 });
        var input = "<b>" + new string('a', 5000) + "<script>alert(1)</script>";

        var output = sanitizer.Sanitize(input);

        StrictHtmlChecker.AssertSafe(output, "oversized");
        Assert.DoesNotContain("alert", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Pathological_inputs_finish_quickly()
    {
        var inputs = new[]
        {
            string.Concat(Enumerable.Repeat("<a href=\"", 20000)),
            string.Concat(Enumerable.Repeat("<!--", 20000)),
            string.Concat(Enumerable.Repeat("<![CDATA[", 20000)),
            string.Concat(Enumerable.Repeat("<b ", 20000)),
            "<" + string.Concat(Enumerable.Repeat("a=\"", 30000)),
            string.Concat(Enumerable.Repeat("&#", 50000)),
            string.Concat(Enumerable.Repeat("&amp", 50000)),
        };

        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var input in inputs)
        {
            StrictHtmlChecker.AssertSafe(_sanitizer.Sanitize(input), "pathological");
        }

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    [Fact]
    public void Random_tag_soup_is_always_safe()
    {
        var random = new Random(20240607);
        string[] fragments =
        [
            "<", ">", "</", "/>", "<b>", "</b>", "<script>", "</script>", "<img ", "src=", "onerror=", "alert(1)", "\"", "'", "`", "=",
            "<a href=", "javascript:", "&#106;", "&#x3a;", "&colon;", "<!--", "-->", "<![CDATA[", "]]>", "<svg>", "</svg>", "<style>",
            "</style>", "<iframe ", "srcdoc=", "\u0000", "\t", "\n", " ", "text", "<p>", "<td>", "<li>", "<table>", "<details>", "&", ";",
        ];

        for (var i = 0; i < 1500; i++)
        {
            var count = random.Next(1, 40);
            var input = string.Concat(Enumerable.Range(0, count).Select(_ => fragments[random.Next(fragments.Length)]));

            StrictHtmlChecker.AssertSafe(_sanitizer.Sanitize(input), input);
        }
    }

    [Fact]
    public void Parse_returns_structured_pieces_for_the_native_preview()
    {
        var pieces = _sanitizer.Parse("a<br>b <kbd>K</kbd> <img src=\"p.png\" alt=\"Alt\" width=\"5\">");

        Assert.Contains(pieces, p => p.Kind == HtmlPieceKind.Void && p.Tag == "br");
        Assert.Contains(pieces, p => p.Kind == HtmlPieceKind.Open && p.Tag == "kbd");
        Assert.Contains(pieces, p => p.Kind == HtmlPieceKind.Close && p.Tag == "kbd");
        Assert.Contains(pieces, p => p.Kind == HtmlPieceKind.Text && p.Text == "K");
        var image = Assert.Single(pieces, p => p.Tag == "img");
        Assert.Equal("p.png", image.GetAttribute("src"));
        Assert.Equal("Alt", image.GetAttribute("alt"));
        Assert.Equal("5", image.GetAttribute("width"));
    }

    [Fact]
    public void ToPlainText_drops_markup()
    {
        var plain = HtmlSanitizer.ToPlainText(_sanitizer.Parse("<p>Hello <b>big</b> &amp; <script>x</script>world</p>"));

        Assert.Equal("Hello big & world", plain.Trim());
    }

    [Fact]
    public void The_allow_lists_are_exposed_and_exclude_dangerous_names()
    {
        foreach (var dangerous in new[] { "script", "style", "iframe", "object", "embed", "form", "input", "link", "meta", "base", "svg" })
        {
            Assert.DoesNotContain(dangerous, HtmlSanitizer.AllowedTagNames);
        }

        Assert.Contains("kbd", HtmlSanitizer.AllowedTagNames);
        Assert.Contains("title", HtmlSanitizer.AllowedAttributeNames("p"));
        Assert.DoesNotContain("style", HtmlSanitizer.AllowedAttributeNames("p"));
        Assert.DoesNotContain("onclick", HtmlSanitizer.AllowedAttributeNames("p"));
        Assert.Contains("href", HtmlSanitizer.AllowedAttributeNames("a"));
        Assert.DoesNotContain("srcset", HtmlSanitizer.AllowedAttributeNames("img"));
    }

    [Fact]
    public void TryParseTag_reads_single_tags_and_rejects_text()
    {
        Assert.True(_sanitizer.TryParseTag("<kbd>", out var open));
        Assert.Equal("kbd", open.Name);
        Assert.False(open.IsClosing);
        Assert.True(_sanitizer.TryParseTag("</kbd>", out var close));
        Assert.True(close.IsClosing);
        Assert.False(_sanitizer.TryParseTag("plain text", out _));
    }
}
