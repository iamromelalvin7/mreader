using System;
using System.Text;
using System.Text.RegularExpressions;

namespace MDreader.Services;

public readonly record struct EditResult(string NewText, int NewSelectionStart, int NewSelectionLength);

public static class MarkdownFormattingService
{
    // WinUI's TextBox inserts '\r' (not '\n') when the user presses Enter, but a file
    // loaded from disk may use '\n' or '\r\n'. Treat all three as line breaks so
    // formatting always targets the line the caret/selection is actually on.
    private static int FindLineStart(string text, int pos)
    {
        int idx = pos - 1;
        while (idx >= 0 && text[idx] != '\n' && text[idx] != '\r') idx--;
        return idx + 1;
    }

    private static int FindLineEnd(string text, int pos)
    {
        int idx = pos;
        while (idx < text.Length && text[idx] != '\n' && text[idx] != '\r') idx++;
        return idx;
    }

    public static EditResult ToggleWrap(string fullText, int selStart, int selLength, string prefix, string suffix)
    {
        string selected = fullText.Substring(selStart, selLength);

        if (selected.StartsWith(prefix, StringComparison.Ordinal)
            && selected.EndsWith(suffix, StringComparison.Ordinal)
            && selected.Length >= prefix.Length + suffix.Length)
        {
            string unwrapped = selected.Substring(prefix.Length, selected.Length - prefix.Length - suffix.Length);
            string newText = fullText.Remove(selStart, selLength).Insert(selStart, unwrapped);
            return new EditResult(newText, selStart, unwrapped.Length);
        }

        if (selLength == 0)
        {
            int lineStart = FindLineStart(fullText, selStart);
            int lineEnd = FindLineEnd(fullText, selStart);
            int open = selStart > 0 ? fullText.LastIndexOf(prefix, selStart - 1, StringComparison.Ordinal) : -1;
            int close = fullText.IndexOf(suffix, selStart, StringComparison.Ordinal);
            if (open >= lineStart && close >= selStart && close + suffix.Length <= lineEnd && open + prefix.Length <= selStart)
            {
                string content = fullText[(open + prefix.Length)..close];
                string unwrappedText = fullText.Remove(close, suffix.Length).Remove(open, prefix.Length);
                return new EditResult(unwrappedText, open, content.Length);
            }

            const string placeholder = "text";
            string newText = fullText.Insert(selStart, prefix + placeholder + suffix);
            return new EditResult(newText, selStart + prefix.Length, placeholder.Length);
        }

        string wrapped = prefix + selected + suffix;
        string newTextWrapped = fullText.Remove(selStart, selLength).Insert(selStart, wrapped);
        return new EditResult(newTextWrapped, selStart + prefix.Length, selected.Length);
    }

    public static EditResult ToggleHeader(string fullText, int caretPos, int level)
    {
        int lineStart = FindLineStart(fullText, caretPos);
        int lineEnd = FindLineEnd(fullText, caretPos);
        string line = fullText[lineStart..lineEnd];

        string stripped = Regex.Replace(line, "^#{1,6}\\s*", "");
        string newHashes = new string('#', level);
        bool alreadyThisLevel = line.StartsWith(newHashes + " ", StringComparison.Ordinal)
            && !line.StartsWith(newHashes + "#", StringComparison.Ordinal);
        string newLine = alreadyThisLevel ? stripped : newHashes + " " + stripped;

        string newText = fullText.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, newLine);
        return new EditResult(newText, lineStart + newLine.Length, 0);
    }

    public static EditResult InsertLink(string fullText, int selStart, int selLength)
    {
        string selected = selLength > 0 ? fullText.Substring(selStart, selLength) : "link text";
        string markdown = "[" + selected + "](url)";
        string newText = fullText.Remove(selStart, selLength).Insert(selStart, markdown);
        int urlStart = selStart + selected.Length + 3; // past "[text]("
        return new EditResult(newText, urlStart, 3);
    }

    public static EditResult ToggleBlockquote(string fullText, int caretPos)
        => ToggleBlockquote(fullText, caretPos, 0);

    public static EditResult ToggleBlockquote(string fullText, int selStart, int selLength)
    {
        var (lineStart, lineEnd) = FindLineBlock(fullText, selStart, selLength);
        string block = fullText[lineStart..lineEnd];
        string newline = block.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : block.Contains('\r') ? "\r" : "\n";
        string[] lines = Regex.Split(block, "\r\n|\r|\n");
        bool quoted = Array.TrueForAll(lines, line => line.Length == 0 || line.StartsWith("> ", StringComparison.Ordinal));
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) continue;
            lines[i] = quoted ? lines[i][2..] : "> " + lines[i];
        }
        string newBlock = string.Join(newline, lines);
        string newText = fullText.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, newBlock);
        return new EditResult(newText, lineStart, newBlock.Length);
    }

    public static EditResult InsertHorizontalRule(string fullText, int caretPos)
    {
        const string rule = "\n\n---\n\n";
        string newText = fullText.Insert(caretPos, rule);
        int newCaret = caretPos + rule.Length;
        return new EditResult(newText, newCaret, 0);
    }

    private static readonly Regex OrderedListPrefix = new("^\\d+\\.\\s", RegexOptions.Compiled);
    private static readonly Regex TaskListPrefix = new("^- \\[[ xX]\\]\\s", RegexOptions.Compiled);

    private static (int LineStart, int LineEnd) FindLineBlock(string fullText, int selStart, int selLength)
    {
        int lineStart = FindLineStart(fullText, selStart);
        int selEnd = selStart + selLength;
        int lineEnd = FindLineEnd(fullText, Math.Max(lineStart, selEnd - 1));
        return (lineStart, lineEnd);
    }

    public static EditResult ToggleListPrefix(string fullText, int selStart, int selLength, bool ordered)
    {
        var (lineStart, lineEnd) = FindLineBlock(fullText, selStart, selLength);

        string block = fullText[lineStart..lineEnd];
        string[] lines = Regex.Split(block, "\r\n|\r|\n");

        bool alreadyListed = Array.TrueForAll(lines, l =>
            l.Length == 0 || (ordered ? OrderedListPrefix.IsMatch(l) : l.StartsWith("- ", StringComparison.Ordinal)));

        string newline = block.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : block.Contains('\r') ? "\r" : "\n";
        var rebuilt = new StringBuilder();
        int orderedNumber = 1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) rebuilt.Append(newline);

            string content = alreadyListed
                ? (ordered ? OrderedListPrefix.Replace(lines[i], "") : Regex.Replace(lines[i], "^- ", ""))
                : lines[i];

            if (alreadyListed)
            {
                rebuilt.Append(content);
            }
            else if (content.Length == 0)
            {
                rebuilt.Append(content);
            }
            else
            {
                rebuilt.Append(ordered ? $"{orderedNumber++}. {content}" : $"- {content}");
            }
        }

        string newBlock = rebuilt.ToString();
        string newText = fullText.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, newBlock);
        return new EditResult(newText, lineStart, newBlock.Length);
    }

    public static EditResult ToggleTaskList(string fullText, int selStart, int selLength)
    {
        var (lineStart, lineEnd) = FindLineBlock(fullText, selStart, selLength);

        string block = fullText[lineStart..lineEnd];
        string[] lines = Regex.Split(block, "\r\n|\r|\n");

        bool alreadyListed = Array.TrueForAll(lines, l => l.Length == 0 || TaskListPrefix.IsMatch(l));

        string newline = block.Contains("\r\n", StringComparison.Ordinal) ? "\r\n"
            : block.Contains('\r') ? "\r" : "\n";
        var rebuilt = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) rebuilt.Append(newline);

            string content = alreadyListed ? TaskListPrefix.Replace(lines[i], "") : lines[i];

            if (alreadyListed || content.Length == 0)
            {
                rebuilt.Append(content);
            }
            else
            {
                rebuilt.Append("- [ ] " + content);
            }
        }

        string newBlock = rebuilt.ToString();
        string newText = fullText.Remove(lineStart, lineEnd - lineStart).Insert(lineStart, newBlock);
        return new EditResult(newText, lineStart, newBlock.Length);
    }

    public static EditResult InsertImage(string fullText, int selStart, int selLength)
    {
        string selected = selLength > 0 ? fullText.Substring(selStart, selLength) : "alt text";
        string markdown = "![" + selected + "](url)";
        string newText = fullText.Remove(selStart, selLength).Insert(selStart, markdown);
        int urlStart = selStart + selected.Length + 4; // past "![alt]("
        return new EditResult(newText, urlStart, 3);
    }

    public static EditResult InsertTable(string fullText, int caretPos)
    {
        const string table = "\n\n| Header 1 | Header 2 | Header 3 |\n| --- | --- | --- |\n| Cell 1 | Cell 2 | Cell 3 |\n\n";
        string newText = fullText.Insert(caretPos, table);
        int headerStart = caretPos + table.IndexOf("Header 1", StringComparison.Ordinal);
        return new EditResult(newText, headerStart, "Header 1".Length);
    }
}
