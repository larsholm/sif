using sif.agent;
using Xunit;

namespace sif.agent.tests;

public sealed class ToolRegistryArgumentTests
{
    [Fact]
    public async Task BashAcceptsCommandAliasesAndNumericStringOptions()
    {
        var result = await ToolRegistry.ExecuteAsync(
            "bash",
            """{"cmd":"echo alias-ok","maxChars":"1000","timeoutSeconds":"2"}""");

        Assert.Contains("alias-ok", result);
    }

    [Fact]
    public async Task ReadAcceptsPathAndLimitAliases()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "sample.txt");
        await File.WriteAllLinesAsync(file, ["first", "second", "third"]);

        var result = await ToolRegistry.ExecuteAsync(
            "read",
            $$"""{"filePath":"{{file}}","skip":1,"maxLines":"1"}""");

        Assert.StartsWith("second", result);
    }

    [Fact]
    public async Task WriteAcceptsPathAndContentAliases()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "written.txt");

        var result = await ToolRegistry.ExecuteAsync(
            "write",
            $$"""{"file":"{{file}}","text":"hello from alias"}""");

        Assert.Contains("Wrote", result);
        Assert.Equal("hello from alias", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task EditAcceptsSearchAndReplacementAliases()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "edit.txt");
        await File.WriteAllTextAsync(file, "before middle after");

        var result = await ToolRegistry.ExecuteAsync(
            "edit",
            $$"""{"filePath":"{{file}}","search":"middle","replacement":"changed"}""");

        Assert.Contains("Edited", result);
        Assert.Equal("before changed after", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task SleepAcceptsMillisecondsAlias()
    {
        var result = await ToolRegistry.ExecuteAsync("sleep", """{"ms":1}""");

        Assert.Contains("Slept for", result);
    }

    [Theory]
    [InlineData("ctx_search", """{"q":"anything","max":"1"}""", "No context hits")]
    [InlineData("ctx_read", """{"contextId":"missing","max_chars":"10"}""", "context id not found")]
    public async Task ContextToolsAcceptCommonAliases(string tool, string arguments, string expected)
    {
        var result = await ToolRegistry.ExecuteAsync(tool, arguments);

        Assert.Contains(expected, result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RoslynFindSymbolsAcceptsQueryAlias()
    {
        var project = Path.Combine(RepositoryRoot(), "sif.agent", "sif.agent.csproj");
        var result = await ToolRegistry.ExecuteAsync(
            "roslyn_find_symbols",
            $$"""{"path":"{{project}}","query":"ToolRegistry"}""");

        Assert.Contains("ToolRegistry", result);
    }

    private static string CreateTempDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sif-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "sif.agent", "sif.agent.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
