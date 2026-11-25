using System;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace MDreader.Services;

public static class MarkdownRenderService
{
    // Older MDreader builds inserted asset filenames verbatim. A space in a Markdown
    // link destination makes the whole image expression invalid unless it is escaped.
    // Keep this compatibility repair deliberately scoped to MDreader's assets folder.
    private static readonly Regex LegacyAssetImagePattern = new(
        @"(?<prefix>!\[[^\]\r\n]*\]\()(?<path>(?:\.{0,2}/)?assets/[^)\r\n<>]*\s[^)\r\n<>]*\.(?:png|jpe?g|gif|webp|svg))(?<suffix>\))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UseYamlFrontMatter()
        .DisableHtml()
        .Build();

    // KaTeX (bundled under Assets/katex, see CLAUDE.md "Math rendering"). Keep the version,
    // the jsDelivr URL and the SRI hashes in sync when updating the bundled files.
    public const string KatexVersion = "0.19.1";
    public const string MathAssetHost = "mdreader-assets.local";
    private const string AppKatexBase = "https://" + MathAssetHost + "/";
    private const string CdnKatexOrigin = "https://cdn.jsdelivr.net";
    private const string CdnKatexBase = CdnKatexOrigin + "/npm/katex@" + KatexVersion + "/dist/";
    private const string KatexJsIntegrity = "sha384-a1WQdjRj162i159OYq041X6I4K8NenkrI09LlPmiHg0YOb7CR+wJOraXwK9abpnE";
    private const string KatexCssIntegrity = "sha384-WwGtaC6ywCHxA5cstpj/3+xIZvTez8dq2IcBQaXRkenHZ7FVVq1ZROtURwfbtTlG";

    /// <param name="standalone">
    /// True for HTML that leaves the app (Export HTML, Copy HTML): it cannot reach the
    /// in-app virtual host, so math loads KaTeX from the pinned jsDelivr CDN (with SRI) instead.
    /// Offline, the raw TeX stays visible.
    /// </param>
    public static string RenderToHtml(string markdown, bool darkTheme = false, double scrollRatio = 0, string? baseHref = null, bool standalone = false)
    {
        const string underlineOpenToken = "MDREADERUNDERLINEOPEN-8D74F";
        const string underlineCloseToken = "MDREADERUNDERLINECLOSE-8D74F";
        string tokenized = NormalizeLegacyAssetImagePaths(markdown)
            .Replace("<u>", underlineOpenToken, StringComparison.OrdinalIgnoreCase)
            .Replace("</u>", underlineCloseToken, StringComparison.OrdinalIgnoreCase);
        string body = Markdown.ToHtml(tokenized, Pipeline)
            .Replace(underlineOpenToken, "<u>", StringComparison.Ordinal)
            .Replace(underlineCloseToken, "</u>", StringComparison.Ordinal);

        string background = darkTheme ? "#202020" : "#ffffff";
        string surface = darkTheme ? "#2b2b2b" : "#f5f5f5";
        string text = darkTheme ? "#f3f3f3" : "#1b1b1b";
        string muted = darkTheme ? "#b8b8b8" : "#5f5f5f";
        string border = darkTheme ? "#4a4a4a" : "#d7d7d7";
        string link = darkTheme ? "#78b7ff" : "#005fb8";
        double safeRatio = Math.Clamp(scrollRatio, 0, 1);
        string baseElement = baseHref is null ? string.Empty : $"<base href=\"{System.Net.WebUtility.HtmlEncode(baseHref)}\" />";
        string errorColor = darkTheme ? "#ff8a80" : "#c42b1c";

        // Markdig's Mathematics extension emits <span class="math">\(..\)</span> and
        // <div class="math">\[..\]</div>. Only pull in KaTeX when the page has math.
        bool hasMath = body.Contains("class=\"math\"", StringComparison.Ordinal);
        string mathSource = standalone ? CdnKatexOrigin : AppKatexBase.TrimEnd('/');
        string mathCsp = hasMath ? " " + mathSource : string.Empty;
        string fontCsp = hasMath ? $" font-src {mathSource};" : string.Empty;
        string katexHead = string.Empty;
        string katexScript = string.Empty;
        if (hasMath)
        {
            string katexBase = standalone ? CdnKatexBase : AppKatexBase;
            string cssSri = standalone ? $" integrity=\"{KatexCssIntegrity}\" crossorigin=\"anonymous\"" : string.Empty;
            string jsSri = standalone ? $" integrity=\"{KatexJsIntegrity}\" crossorigin=\"anonymous\"" : string.Empty;
            katexHead = $"<link rel=\"stylesheet\" href=\"{katexBase}katex.min.css\"{cssSri} />";
            // Synchronous on purpose: the inline script below must run after KaTeX is defined.
            katexScript = $"<script nonce=\"mdreader\" src=\"{katexBase}katex.min.js\"{jsSri}></script>";
        }

        return $$"""
            <!DOCTYPE html>
            <html>
            <head>
                <meta charset="utf-8" />
                <meta name="viewport" content="width=device-width, initial-scale=1" />
                {{baseElement}}
                <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data: https: http: file:; style-src 'unsafe-inline'{{mathCsp}}; script-src 'nonce-mdreader'{{mathCsp}};{{fontCsp}} frame-src 'none'; media-src 'none'; object-src 'none'; base-uri https://mdreader.local; form-action 'none'" />
                {{katexHead}}
                <style>
                    :root { color-scheme: {{(darkTheme ? "dark" : "light")}}; }
                    * { box-sizing: border-box; }
                    html { background: {{background}}; }
                    body { font-family: "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; font-size: 15px; line-height: 1.68; color: {{text}}; background: {{background}}; max-width: 860px; margin: 0 auto; padding: 32px 40px 80px; overflow-wrap: anywhere; }
                    h1, h2, h3, h4, h5, h6 { line-height: 1.25; margin: 1.6em 0 .55em; }
                    h1 { font-size: 2em; border-bottom: 1px solid {{border}}; padding-bottom: .3em; }
                    h2 { font-size: 1.55em; border-bottom: 1px solid {{border}}; padding-bottom: .22em; }
                    h3 { font-size: 1.25em; }
                    p, ul, ol, blockquote, pre, table { margin: 0 0 1em; }
                    a { color: {{link}}; text-underline-offset: 2px; }
                    a:focus-visible { outline: 2px solid {{link}}; outline-offset: 3px; border-radius: 2px; }
                    code, pre { font-family: "Cascadia Mono", Consolas, monospace; }
                    pre { background: {{surface}}; padding: 14px 16px; border: 1px solid {{border}}; border-radius: 6px; overflow-x: auto; }
                    code { background: {{surface}}; padding: 2px 5px; border-radius: 4px; }
                    pre code { background: none; padding: 0; }
                    blockquote { border-left: 3px solid {{link}}; margin-left: 0; padding: 2px 0 2px 16px; color: {{muted}}; }
                    table { border-collapse: collapse; display: block; max-width: 100%; overflow-x: auto; }
                    th, td { border: 1px solid {{border}}; padding: 7px 12px; text-align: left; }
                    th { background: {{surface}}; }
                    img { max-width: 100%; height: auto; border-radius: 6px; }
                    hr { border: 0; border-top: 1px solid {{border}}; margin: 2em 0; }
                    input[type=checkbox] { accent-color: {{link}}; }
                    .math:not(.math-rendered) { font-family: "Cascadia Mono", Consolas, monospace; font-size: .92em; background: {{surface}}; border-radius: 4px; padding: 1px 4px; }
                    div.math:not(.math-rendered) { display: block; white-space: pre-wrap; padding: 10px 14px; margin: 0 0 1em; border: 1px solid {{border}}; }
                    div.math.math-rendered { margin: 0 0 1em; }
                    .katex { overflow-wrap: normal; }
                    .katex-display { margin: .6em 0; padding: 4px 2px; overflow-x: auto; overflow-y: hidden; }
                    .katex-error { color: {{errorColor}} !important; }
                    @media (max-width: 650px) { body { padding: 22px 20px 60px; } }
                    @media print { body { max-width: none; padding: 0; color: #000; background: #fff; } }
                </style>
            </head>
            <body>
            {{body}}
            {{katexScript}}
            <script nonce="mdreader">
            (() => {
                const restoreScroll = () => requestAnimationFrame(() => window.scrollTo(0, {{safeRatio.ToString(System.Globalization.CultureInfo.InvariantCulture)}} * Math.max(0, document.documentElement.scrollHeight - innerHeight)));
                const math = document.querySelectorAll('span.math, div.math');
                if (!math.length || !window.katex) { restoreScroll(); return; }
                for (const el of math) {
                    const display = el.tagName === 'DIV';
                    const tex = el.textContent.trim().replace(display ? /^\\\[|\\\]$/g : /^\\\(|\\\)$/g, '').trim();
                    try {
                        katex.render(tex, el, { displayMode: display, throwOnError: false, errorColor: '{{errorColor}}' });
                        el.classList.add('math-rendered');
                    } catch { /* leave the raw TeX visible */ }
                }
                // Typesetting and KaTeX font loading change the page height: restore the
                // scroll position only once both have settled.
                (document.fonts ? document.fonts.ready : Promise.resolve()).then(restoreScroll, restoreScroll);
            })();
            </script>
            </body>
            </html>
            """;
    }

    internal static string NormalizeLegacyAssetImagePaths(string markdown)
    {
        var result = new StringBuilder(markdown.Length);
        bool inFence = false;
        char fenceCharacter = '\0';
        int fenceLength = 0;

        int lineStart = 0;
        while (lineStart < markdown.Length)
        {
            int lineEnd = lineStart;
            while (lineEnd < markdown.Length && markdown[lineEnd] is not '\r' and not '\n') lineEnd++;
            string line = markdown[lineStart..lineEnd];

            bool isFence = TryGetFence(line, out char marker, out int markerLength);
            if (inFence)
            {
                result.Append(line);
                if (isFence && marker == fenceCharacter && markerLength >= fenceLength)
                    inFence = false;
            }
            else if (isFence)
            {
                result.Append(line);
                inFence = true;
                fenceCharacter = marker;
                fenceLength = markerLength;
            }
            else
            {
                result.Append(LegacyAssetImagePattern.Replace(line, EncodeLegacyAssetImagePath));
            }

            if (lineEnd < markdown.Length && markdown[lineEnd] == '\r')
            {
                result.Append('\r');
                lineEnd++;
            }
            if (lineEnd < markdown.Length && markdown[lineEnd] == '\n')
            {
                result.Append('\n');
                lineEnd++;
            }
            lineStart = lineEnd;
        }

        return result.ToString();
    }

    private static string EncodeLegacyAssetImagePath(Match match)
    {
        string path = match.Groups["path"].Value.Replace('\\', '/');
        string[] segments = path.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            // Unescape first so an already encoded portion is not encoded twice.
            segments[i] = Uri.EscapeDataString(Uri.UnescapeDataString(segments[i]));
        }

        return match.Groups["prefix"].Value
            + string.Join('/', segments)
            + match.Groups["suffix"].Value;
    }

    private static bool TryGetFence(string line, out char marker, out int length)
    {
        int index = 0;
        while (index < line.Length && index < 4 && line[index] == ' ') index++;
        if (index > 3 || index >= line.Length || line[index] is not ('`' or '~'))
        {
            marker = '\0';
            length = 0;
            return false;
        }

        marker = line[index];
        int start = index;
        while (index < line.Length && line[index] == marker) index++;
        length = index - start;
        return length >= 3;
    }
}
