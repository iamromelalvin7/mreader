using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace MDreader.Services;

public sealed record HeadingInfo(int Level, string Text, int CharacterIndex, int LineNumber)
{
    // The outline is plain text, so drop the $ math delimiters ("Comparison ($n$ = devices)" -> "Comparison (n = devices)").
    public string DisplayText => $"{new string(' ', Math.Max(0, Level - 1) * 2)}{Text.Replace("$", string.Empty)}";
}

public static partial class MarkdownDocumentService
{
    [GeneratedRegex("^(#{1,6})\\s+(.+?)\\s*#*\\s*$")]
    private static partial Regex HeadingRegex();

    public static IReadOnlyList<HeadingInfo> GetHeadings(string markdown)
    {
        var headings = new List<HeadingInfo>();
        bool inFence = false;
        bool inFrontMatter = false;
        int index = 0;
        int lineNumber = 1;

        foreach (string line in Regex.Split(markdown, "(?<=\\r\\n)|(?<=\\r)|(?<=\\n)"))
        {
            string content = line.TrimEnd('\r', '\n');
            if (lineNumber == 1 && content == "---") inFrontMatter = true;
            else if (inFrontMatter && content is "---" or "...") inFrontMatter = false;
            else if (!inFrontMatter && (content.StartsWith("```", StringComparison.Ordinal) || content.StartsWith("~~~", StringComparison.Ordinal))) inFence = !inFence;
            else if (!inFrontMatter && !inFence)
            {
                Match match = HeadingRegex().Match(content);
                if (match.Success) headings.Add(new HeadingInfo(match.Groups[1].Length, match.Groups[2].Value.Trim(), index, lineNumber));
            }

            index += line.Length;
            lineNumber++;
        }
        return headings;
    }

    public static string BuildTableOfContents(string markdown)
    {
        var builder = new StringBuilder();
        foreach (HeadingInfo heading in GetHeadings(markdown))
        {
            string slug = Regex.Replace(heading.Text.ToLowerInvariant(), @"[^\p{L}\p{N}\s-]", "");
            slug = Regex.Replace(slug.Trim(), @"\s+", "-");
            builder.Append(' ', Math.Max(0, heading.Level - 1) * 2).Append("- [").Append(heading.Text).Append("](#").Append(slug).Append(")\n");
        }
        return builder.ToString();
    }

    public static int CountWords(string text) => Regex.Matches(text, @"\b[\p{L}\p{N}][\p{L}\p{N}'’-]*\b").Count;

    public static string DetectLineEndings(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "CRLF" : text.Contains('\r') ? "CR" : "LF";
}
