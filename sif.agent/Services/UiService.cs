using ConsoleMarkdownRenderer;
using ConsoleMarkdownRenderer.Styling;
using Spectre.Console;

namespace sif.agent;

internal static class UiService
{
    public static DisplayOptions MarkdownOptions { get; } = new DisplayOptions
    {
        // Headers: use clean, bold terminal colors
        Header = new TextStyle(TextDecoration.Bold, TextColor.Blue),
        Headers = new List<TextStyle>
        {
            new TextStyle(TextDecoration.Bold, TextColor.Blue),    // H1
            new TextStyle(TextDecoration.Bold, TextColor.Blue),    // H2
            new TextStyle(TextDecoration.Bold, TextColor.Green),   // H3
            new TextStyle(TextDecoration.Bold, TextColor.Yellow),  // H4
            new TextStyle(TextDecoration.Bold, TextColor.Yellow),  // H5
            new TextStyle(TextDecoration.Bold, TextColor.Yellow),  // H6
        },

        // Code: use standard terminal colors
        CodeInLine = new TextStyle(TextDecoration.None, TextColor.Yellow),
        CodeBlock = new TextStyle(TextDecoration.None), // Default foreground

        // Bold & Italic
        Bold = new TextStyle(TextDecoration.Bold),
        Italic = new TextStyle(TextDecoration.Italic),

        // Blockquote
        QuotedBlock = new TextStyle(TextDecoration.Italic), // Default foreground

        // Don't clutter with too many '#'
        WrapHeader = false
    };

    public static async Task DisplayMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return;
        
        try
        {
            await Displayer.DisplayMarkdownAsync(markdown, options: MarkdownOptions);
        }
        catch
        {
            // Fallback if renderer fails
            AnsiConsole.MarkupLine(markdown.EscapeMarkup());
        }
    }
}
