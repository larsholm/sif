using System.Text;
using System.Text.Json;
using sif.agent;
using sif.agent.Services.Tools;
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
    public async Task EditRequiresOneMatchByDefault()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "ambiguous.txt");
        await File.WriteAllTextAsync(file, "same middle same");

        var result = await ToolRegistry.ExecuteAsync(
            "edit",
            JsonSerializer.Serialize(new { path = file, oldText = "same", newText = "changed" }));

        Assert.Contains("matched 2 occurrences", result);
        Assert.Contains("replaceAll", result);
        Assert.Equal("same middle same", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task EditReplacesEveryMatchWhenExplicitlyRequested()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "replace-all.txt");
        await File.WriteAllTextAsync(file, "same middle same");

        var result = await ToolRegistry.ExecuteAsync(
            "edit",
            JsonSerializer.Serialize(new { path = file, oldText = "same", newText = "changed", replaceAll = true }));

        Assert.Contains("Replaced 2 occurrence(s)", result);
        Assert.Equal("changed middle changed", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task EditNormalizesIncomingNewLinesToFileStyle()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "crlf.txt");
        await File.WriteAllTextAsync(file, "before\r\nold one\r\nold two\r\nafter", new UTF8Encoding(false));

        var result = await ToolRegistry.ExecuteAsync(
            "edit",
            JsonSerializer.Serialize(new
            {
                path = file,
                oldText = "old one\nold two",
                newText = "new one\nnew two"
            }));

        Assert.Contains("Replaced 1 occurrence(s)", result);
        Assert.Equal("before\r\nnew one\r\nnew two\r\nafter", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task EditPreservesUtf16EncodingAndBom()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "utf16.txt");
        var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true);
        await File.WriteAllTextAsync(file, "before old after", encoding);

        var result = await ToolRegistry.ExecuteAsync(
            "edit",
            JsonSerializer.Serialize(new { path = file, oldText = "old", newText = "new" }));

        var bytes = await File.ReadAllBytesAsync(file);
        Assert.Contains("Replaced 1 occurrence(s)", result);
        Assert.True(bytes.AsSpan().StartsWith(encoding.GetPreamble()));
        Assert.Equal("before new after", encoding.GetString(bytes.AsSpan(encoding.GetPreamble().Length)));
    }

    [Fact]
    public async Task SleepAcceptsMillisecondsAlias()
    {
        var result = await ToolRegistry.ExecuteAsync("sleep", """{"ms":1}""");

        Assert.Contains("Slept for", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    public async Task ToolsReturnErrorsForNonObjectArguments(string arguments)
    {
        var result = await ToolRegistry.ExecuteAsync("roslyn_get_diagnostics", arguments);

        Assert.Contains("expected JSON object arguments", result);
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

    [Fact]
    public async Task RoslynFindSymbolsAcceptsProjectDirectory()
    {
        var projectDirectory = Path.Combine(RepositoryRoot(), "sif.agent");
        var result = await ToolRegistry.ExecuteAsync(
            "roslyn_find_symbols",
            $$"""{"path":"{{projectDirectory}}","query":"ToolRegistry"}""");

        Assert.Contains("ToolRegistry", result);
    }

    [Fact]
    public async Task AmbientRoslynContextReportsActiveFileDiagnostics()
    {
        var dir = CreateTempDirectory();
        var file = Path.Combine(dir, "Broken.cs");
        await File.WriteAllTextAsync(file, """
            namespace Demo;

            public class Broken
            {
                public void Run()
                {
                    var value =
                }
            }
            """);

        var result = RoslynTools.BuildAmbientContext(file, "5");

        Assert.NotNull(result);
        Assert.Contains("""<roslyn_context source="ambient">""", result);
        Assert.Contains("Nearest declaration: method Run", result);
        Assert.Contains("Syntax diagnostics:", result);
        Assert.Contains("Error", result);
    }

    [Fact]
    public async Task RoslynGetDiagnosticsFiltersSeverityAndReportsLoadFailures()
    {
        var dir = CreateTempDirectory();
        await File.WriteAllTextAsync(Path.Combine(dir, "broken.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../missing/missing.csproj" />
              </ItemGroup>
            </Project>
            """);
        await File.WriteAllTextAsync(Path.Combine(dir, "Bad.cs"), """
            using System;

            namespace Demo;

            public class Bad
            {
                public int Run() { return "not an int"; }
            }
            """);

        var result = await ToolRegistry.ExecuteAsync(
            "roslyn_get_diagnostics",
            $$"""{"path":"{{dir}}"}""");

        Assert.Contains("CS0029", result);
        Assert.Contains("LoadFailures", result);
        Assert.Contains("missing.csproj", result);
        // Hidden-severity noise (e.g. CS8019 unnecessary using) must be filtered out.
        Assert.DoesNotContain("Hidden", result);
        Assert.DoesNotContain("CS8019", result);
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
