using System.Globalization;
using Spectre.Console;

namespace sif.agent.Services;

internal readonly record struct TerminalCursorPosition(int Row, int Column);

internal static class TerminalInputLayout
{
    internal static int GetCellWidth(string text) => Math.Max(text.GetCellWidth(), 0);

    internal static IReadOnlyList<string> WrapLine(string line, int maximumCellWidth)
    {
        maximumCellWidth = Math.Max(maximumCellWidth, 1);
        if (line.Length == 0)
            return [""];

        var segments = new List<string>();
        var current = new System.Text.StringBuilder();
        var currentWidth = 0;
        var elements = StringInfo.GetTextElementEnumerator(line);

        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            var elementWidth = GetCellWidth(element);

            if (current.Length > 0 && currentWidth + elementWidth > maximumCellWidth)
            {
                segments.Add(current.ToString());
                current.Clear();
                currentWidth = 0;
            }

            current.Append(element);
            currentWidth += elementWidth;
        }

        segments.Add(current.ToString());
        return segments;
    }

    internal static int RowsForLine(string line, int maximumCellWidth) =>
        WrapLine(line, maximumCellWidth).Count;

    internal static TerminalCursorPosition GetCursorPosition(
        string text,
        int cursorIndex,
        int maximumCellWidth,
        int indent)
    {
        cursorIndex = Math.Clamp(cursorIndex, 0, text.Length);
        var lines = text.Split('\n');
        var textUpToCursor = text[..cursorIndex];
        var cursorLine = textUpToCursor.Count(character => character == '\n');
        var lastNewline = textUpToCursor.LastIndexOf('\n');
        var cursorOffset = lastNewline == -1
            ? cursorIndex
            : cursorIndex - lastNewline - 1;

        var row = 0;
        for (var index = 0; index < cursorLine; index++)
            row += RowsForLine(lines[index], maximumCellWidth);

        var currentLine = lines[Math.Min(cursorLine, lines.Length - 1)];
        var wrapped = WrapLine(currentLine, maximumCellWidth);
        var segmentStart = 0;

        for (var index = 0; index < wrapped.Count; index++)
        {
            var segment = wrapped[index];
            var segmentEnd = segmentStart + segment.Length;

            if (cursorOffset < segmentEnd)
            {
                var prefix = segment[..Math.Max(cursorOffset - segmentStart, 0)];
                return new TerminalCursorPosition(row + index, indent + GetCellWidth(prefix));
            }

            if (cursorOffset == segmentEnd)
            {
                if (index < wrapped.Count - 1)
                    return new TerminalCursorPosition(row + index + 1, indent);

                return new TerminalCursorPosition(row + index, indent + GetCellWidth(segment));
            }

            segmentStart = segmentEnd;
        }

        return new TerminalCursorPosition(row, indent);
    }
}
