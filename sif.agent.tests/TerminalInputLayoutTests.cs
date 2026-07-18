using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class TerminalInputLayoutTests
{
    [Theory]
    [InlineData("hello", 5)]
    [InlineData("中文", 4)]
    [InlineData("A中B", 4)]
    [InlineData("e\u0301", 1)]
    public void CellWidthUsesTerminalColumns(string text, int expectedWidth)
    {
        Assert.Equal(expectedWidth, TerminalInputLayout.GetCellWidth(text));
    }

    [Fact]
    public void WrapLineUsesCellWidthWithoutSplittingWideCharacters()
    {
        var segments = TerminalInputLayout.WrapLine("ab中文cd", 4);

        Assert.Equal(["ab中", "文cd"], segments);
        Assert.All(segments, segment => Assert.True(TerminalInputLayout.GetCellWidth(segment) <= 4));
    }

    [Theory]
    [InlineData(0, 0, 2)]
    [InlineData(1, 0, 3)]
    [InlineData(2, 0, 4)]
    [InlineData(3, 1, 2)]
    [InlineData(4, 1, 4)]
    public void CursorPositionTracksMixedWidthTextAcrossWrappedRows(
        int cursorIndex,
        int expectedRow,
        int expectedColumn)
    {
        var position = TerminalInputLayout.GetCursorPosition("ab中文", cursorIndex, 4, 2);

        Assert.Equal(expectedRow, position.Row);
        Assert.Equal(expectedColumn, position.Column);
    }

    [Fact]
    public void CursorPositionIncludesRowsFromEarlierLogicalLines()
    {
        var position = TerminalInputLayout.GetCursorPosition("中文A\n中B", 6, 4, 2);

        Assert.Equal(2, position.Row);
        Assert.Equal(5, position.Column);
    }
}
