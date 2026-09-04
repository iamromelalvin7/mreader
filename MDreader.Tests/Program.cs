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
    })
};

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
