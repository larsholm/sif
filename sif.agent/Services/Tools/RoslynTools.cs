using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.FindSymbols;

namespace sif.agent.Services.Tools;

public static class RoslynTools
{
    private static string? GetDefaultSolutionOrProject()
    {
        var dir = Directory.GetCurrentDirectory();
        var sln = Directory.GetFiles(dir, "*.sln").FirstOrDefault();
        if (sln != null) return sln;
        return Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
    }

    public static async Task<string> FindSymbolsAsync(string? path, string name)
    {
        path = string.IsNullOrWhiteSpace(path) ? GetDefaultSolutionOrProject() : path;
        if (string.IsNullOrWhiteSpace(path))
            return "Error: No solution or project file found.";

        using var workspace = MSBuildWorkspace.Create();
        var projects = path.EndsWith(".sln")
            ? (await workspace.OpenSolutionAsync(path)).Projects
            : ImmutableArray.Create(await workspace.OpenProjectAsync(path));

        List<ISymbol> allSymbols = new();
        foreach (var project in projects)
        {
            var symbols = await SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: true, SymbolFilter.All);
            allSymbols.AddRange(symbols);
        }

        var result = allSymbols.Select(s => new
        {
            Name = s.Name,
            Kind = s.Kind.ToString(),
            Location = s.Locations.FirstOrDefault()?.GetLineSpan().ToString() ?? "Unknown"
        });

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }

    public static async Task<string> GetDiagnosticsAsync(string? projectPath)
    {
        projectPath = string.IsNullOrWhiteSpace(projectPath) ? GetDefaultSolutionOrProject() : projectPath;
        if (string.IsNullOrWhiteSpace(projectPath))
            return "Error: No solution or project file found.";

        using var workspace = MSBuildWorkspace.Create();
        var project = await workspace.OpenProjectAsync(projectPath);
        var compilation = await project.GetCompilationAsync();

        if (compilation == null) return "Could not compile project.";

        var diagnostics = compilation.GetDiagnostics();
        var result = diagnostics.Select(d => new
        {
            Id = d.Id,
            Severity = d.Severity.ToString(),
            Message = d.GetMessage(),
            Location = d.Location.GetLineSpan().ToString()
        });

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
    }
}
