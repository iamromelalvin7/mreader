using MDreader.Services;

var tests = new (string Name, Action Run)[]
{
    ("Wrap selection", () => AssertEdit(MarkdownFormattingService.ToggleWrap("hello", 0, 5, "**", "**"), "**hello**", 2, 5)),
    ("Unwrap exact selection", () => AssertEdit(MarkdownFormattingService.ToggleWrap("**hello**", 0, 9, "**", "**"), "hello", 0, 5)),
    ("Unwrap around caret", () => AssertEdit(MarkdownFormattingService.ToggleWrap("say **hello** now", 8, 0, "**", "**"), "say hello now", 4, 5)),
    ("Header respects CRLF", () => AssertEdit(MarkdownFormattingService.ToggleHeader("one\r\ntwo", 6, 2), "one\r\n## two", 11, 0)),
    ("List preserves CRLF", () => AssertEqual("- one\r\n- two", MarkdownFormattingService.ToggleListPrefix("one\r\ntwo", 0, 8, false).NewText)),
    ("Ordered list skips blank number", () => AssertEqual("1. one\n\n2. two", MarkdownFormattingService.ToggleListPrefix("one\n\ntwo", 0, 8, true).NewText)),
    ("Task list preserves CR", () => AssertEqual("- [ ] one\r- [ ] two", MarkdownFormattingService.ToggleTaskList("one\rtwo", 0, 7).NewText)),
    ("Multiline blockquote", () => AssertEqual("> one\n> two", MarkdownFormattingService.ToggleBlockquote("one\ntwo", 0, 7).NewText)),
    ("Outline ignores front matter and fences", () =>
    {
        var headings = MarkdownDocumentService.GetHeadings("---\ntitle: X\n---\n# Real\n```\n# Fake\n```\n## Child");
        AssertEqual(2, headings.Count);
        AssertEqual("Real", headings[0].Text);
        AssertEqual("Child", headings[1].Text);
    }),
    ("TOC creates anchors", () => AssertEqual("- [Hello World](#hello-world)\n", MarkdownDocumentService.BuildTableOfContents("# Hello World"))),
    ("Word count handles apostrophes", () => AssertEqual(3, MarkdownDocumentService.CountWords("It's a test."))),
    ("Preview repairs legacy image paths with spaces", () =>
    {
        string html = MarkdownRenderService.RenderToHtml("![diagram](assets/my diagram, final.png)");
        AssertContains("<img src=\"assets/my%20diagram%2C%20final.png\" alt=\"diagram\"", html);
    }),
    ("Preview keeps encoded image paths stable", () =>
    {
        string html = MarkdownRenderService.RenderToHtml("![diagram](assets/my%20diagram.png)");
        AssertContains("<img src=\"assets/my%20diagram.png\" alt=\"diagram\"", html);
    }),
    ("Preview leaves fenced Markdown examples unchanged", () =>
    {
        const string markdown = "```markdown\n![diagram](assets/my diagram.png)\n```";
        AssertContains("![diagram](assets/my diagram.png)", MarkdownRenderService.RenderToHtml(markdown));
    }),
    ("Math loads bundled KaTeX from the asset host", () =>
    {
        string html = MarkdownRenderService.RenderToHtml("Delay $d = L/R$\n\n$$\nx^2\n$$");
        AssertContains("<span class=\"math\">", html);
        AssertContains("<div class=\"math\">", html);
        AssertContains("src=\"https://mdreader-assets.local/katex.min.js\"", html);
        AssertMatches(@"script-src 'nonce-[0-9A-F]{32}' https://mdreader-assets\.local;", html);
        AssertContains("font-src https://mdreader-assets.local;", html);
        AssertContains("style-src 'unsafe-inline' https://mdreader-assets.local;", html);
    }),
    ("Exported math uses the pinned CDN with SRI", () =>
    {
        string html = MarkdownRenderService.RenderToHtml("$x$", standalone: true);
        AssertContains($"https://cdn.jsdelivr.net/npm/katex@{MarkdownRenderService.KatexVersion}/dist/katex.min.js\" integrity=\"sha384-", html);
        AssertContains("font-src https://cdn.jsdelivr.net;", html);
        if (html.Contains("mdreader-assets.local")) throw new InvalidOperationException("Export must not reference the in-app asset host");
    }),
    ("Raw HTML like <details> renders", () =>
    {
        string html = MarkdownRenderService.RenderToHtml("<details>\n<summary><b>Answers</b></summary>\n\nA switch.\n\n</details>");
        AssertContains("<details>\n<summary><b>Answers</b></summary>", html);
        AssertContains("<p>A switch.</p>", html);
    }),
    ("Raw HTML cannot add scripts, frames or forms", () =>
    {
        string html = MarkdownRenderService.RenderToHtml("<script nonce=\"mdreader\">alert(1)</script>\n\n<iframe src=\"https://x\"></iframe> <form></form> <meta http-equiv=\"refresh\">");
        foreach (string bad in new[] { "<script nonce=\"mdreader\">alert", "<iframe", "<form", "<meta http-equiv=\"refresh" })
            if (html.Contains(bad)) throw new InvalidOperationException($"Unsafe tag survived: {bad}");
        AssertContains("&lt;iframe", html);
    }),
    ("No math keeps the original tight CSP", () =>
    {
        string html = MarkdownRenderService.RenderToHtml("# Plain");
        AssertMatches(@"style-src 'unsafe-inline'; script-src 'nonce-[0-9A-F]{32}'; frame-src", html);
        if (html.Contains("katex.min")) throw new InvalidOperationException("KaTeX should not load without math");
    })
};

// Harness: `dotnet run -- --dump-math <input.md> <out-dir>` writes the preview HTML (dark,
// light, standalone) for headless browser checks. See CLAUDE.md "Math rendering".
if (args.Length == 3 && args[0] == "--dump-math")
{
    string md = File.ReadAllText(args[1]);
    Directory.CreateDirectory(args[2]);
    File.WriteAllText(Path.Combine(args[2], "dark.html"), MarkdownRenderService.RenderToHtml(md, darkTheme: true));
    File.WriteAllText(Path.Combine(args[2], "light.html"), MarkdownRenderService.RenderToHtml(md, darkTheme: false));
    File.WriteAllText(Path.Combine(args[2], "scroll.html"), MarkdownRenderService.RenderToHtml(md, scrollRatio: 0.5));
    File.WriteAllText(Path.Combine(args[2], "export.html"), MarkdownRenderService.RenderToHtml(md, standalone: true));
    Console.WriteLine($"Wrote HTML to {args[2]}");
    return 0;
}

int failed = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {test.Name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failed}/{tests.Length} tests passed");
return failed == 0 ? 0 : 1;

static void AssertEdit(EditResult result, string text, int start, int length)
{
    AssertEqual(text, result.NewText);
    AssertEqual(start, result.NewSelectionStart);
    AssertEqual(length, result.NewSelectionLength);
}

static void AssertEqual<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected [{expected}], got [{actual}]");
}

static void AssertContains(string expected, string actual)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected to find [{expected}]");
}

static void AssertMatches(string pattern, string actual)
{
    if (!System.Text.RegularExpressions.Regex.IsMatch(actual, pattern))
        throw new InvalidOperationException($"Expected to match [{pattern}]");
}
