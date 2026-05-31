using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.FindSymbols;

namespace sif.agent.Services.Tools;

public static class RoslynTools
{
    private const int MaxAmbientDiagnostics = 20;

    private static string? GetDefaultSolutionOrProject()
    {
        var dir = Directory.GetCurrentDirectory();
        var sln = Directory.GetFiles(dir, "*.sln").FirstOrDefault();
        if (sln != null) return sln;
        return Directory.GetFiles(dir, "*.csproj").FirstOrDefault();
    }

    private static string? ResolveSolutionOrProjectPath(string? path, out string? error)
    {
        error = null;
        path = string.IsNullOrWhiteSpace(path) ? GetDefaultSolutionOrProject() : path;

        if (string.IsNullOrWhiteSpace(path))
            return null;

        path = Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(path, Directory.GetCurrentDirectory());

        if (!Directory.Exists(path))
            return path;

        var solutions = Directory.GetFiles(path, "*.sln");
        if (solutions.Length == 1)
            return solutions[0];

        if (solutions.Length > 1)
        {
            error = $"Error: Directory contains multiple solution files: {path}";
            return null;
        }

        var projects = Directory.GetFiles(path, "*.csproj");
        if (projects.Length == 1)
            return projects[0];

        if (projects.Length > 1)
        {
            error = $"Error: Directory contains multiple project files: {path}";
            return null;
        }

        error = $"Error: No solution or project file found in directory: {path}";
        return null;
    }

    public static string? BuildAmbientContext(string? filePath, string? line)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            !filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return null;

        var path = Path.IsPathRooted(filePath)
            ? filePath
            : Path.GetFullPath(filePath, Directory.GetCurrentDirectory());

        if (!File.Exists(path))
            return null;

        string source;
        try
        {
            source = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        var tree = CSharpSyntaxTree.ParseText(source, path: path);
        var root = tree.GetRoot();
        var diagnostics = tree.GetDiagnostics()
            .Where(d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning)
            .OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Character)
            .Take(MaxAmbientDiagnostics)
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                var pos = span.StartLinePosition;
                return $"{d.Severity} {d.Id} at {Path.GetFileName(path)}:{pos.Line + 1}:{pos.Character + 1}: {d.GetMessage()}";
            })
            .ToArray();

        var sb = new StringBuilder();
        sb.AppendLine("<roslyn_context source=\"ambient\">");
        sb.AppendLine($"File: {path}");

        var declaration = TryFindDeclarationAtLine(root, source, line);
        if (!string.IsNullOrWhiteSpace(declaration))
            sb.AppendLine($"Nearest declaration: {declaration}");

        if (diagnostics.Length == 0)
        {
            sb.AppendLine("Syntax diagnostics: none");
        }
        else
        {
            sb.AppendLine("Syntax diagnostics:");
            foreach (var diagnostic in diagnostics)
                sb.AppendLine($"- {diagnostic}");
        }

        sb.AppendLine("</roslyn_context>");
        return sb.ToString().TrimEnd();
    }

    private static string? TryFindDeclarationAtLine(SyntaxNode root, string source, string? line)
    {
        if (!int.TryParse(line, out var lineNumber) || lineNumber <= 0)
            return null;

        var text = root.SyntaxTree.GetText();
        if (lineNumber > text.Lines.Count)
            return null;

        var position = text.Lines[lineNumber - 1].Start;
        if (position >= source.Length)
            position = Math.Max(0, source.Length - 1);

        var token = root.FindToken(position);
        return token.Parent?
            .AncestorsAndSelf()
            .Select(DescribeDeclaration)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? DescribeDeclaration(SyntaxNode node)
    {
        return node switch
        {
            MethodDeclarationSyntax method => $"method {method.Identifier.ValueText}",
            ConstructorDeclarationSyntax constructor => $"constructor {constructor.Identifier.ValueText}",
            ClassDeclarationSyntax type => $"class {type.Identifier.ValueText}",
            RecordDeclarationSyntax type => $"record {type.Identifier.ValueText}",
            StructDeclarationSyntax type => $"struct {type.Identifier.ValueText}",
            InterfaceDeclarationSyntax type => $"interface {type.Identifier.ValueText}",
            EnumDeclarationSyntax type => $"enum {type.Identifier.ValueText}",
            PropertyDeclarationSyntax property => $"property {property.Identifier.ValueText}",
            FieldDeclarationSyntax field => $"field {string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.ValueText))}",
            LocalFunctionStatementSyntax localFunction => $"local function {localFunction.Identifier.ValueText}",
            _ => null
        };
    }

    public static async Task<string> FindSymbolsAsync(string? path, string name)
    {
        path = ResolveSolutionOrProjectPath(path, out var error);
        if (error != null)
            return error;
        if (string.IsNullOrWhiteSpace(path))
            return "Error: No solution or project file found.";

        using var workspace = MSBuildWorkspace.Create();
        var projects = path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
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
        projectPath = ResolveSolutionOrProjectPath(projectPath, out var error);
        if (error != null)
            return error;
        if (string.IsNullOrWhiteSpace(projectPath))
            return "Error: No solution or project file found.";

        using var workspace = MSBuildWorkspace.Create();
        var projects = projectPath.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            ? (await workspace.OpenSolutionAsync(projectPath)).Projects
            : ImmutableArray.Create(await workspace.OpenProjectAsync(projectPath));

        var allDiagnostics = new List<object>();
        foreach (var project in projects)
        {
            var compilation = await project.GetCompilationAsync();

            if (compilation == null)
                return $"Could not compile project: {project.Name}";

            allDiagnostics.AddRange(compilation.GetDiagnostics().Select(d => new
            {
                Project = project.Name,
                Id = d.Id,
                Severity = d.Severity.ToString(),
                Message = d.GetMessage(),
                Location = d.Location.GetLineSpan().ToString()
            }));
        }

        return JsonSerializer.Serialize(allDiagnostics, new JsonSerializerOptions { WriteIndented = true });
    }
}
