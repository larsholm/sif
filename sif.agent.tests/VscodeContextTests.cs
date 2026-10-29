using System.Text.Json;
using Xunit;

namespace sif.agent.tests;

public sealed class VscodeContextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sif-vscode-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousContextFile = Environment.GetEnvironmentVariable("SIF_VSCODE_CONTEXT_FILE");

    public VscodeContextTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SIF_VSCODE_CONTEXT_FILE", _previousContextFile);
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AppendToUserMessagePointsAtCapturedDebugConsoleOutput()
    {
        var debugLog = Path.Combine(_dir, "debug-console.log");
        File.WriteAllText(debugLog, "Unhandled exception. System.InvalidOperationException: boom\n");
        UseContextFile(debugLog);

        var message = VscodeContext.AppendToUserMessage("why did it crash?");

        Assert.Contains($"Debug console output: {debugLog}", message);
        Assert.Equal(debugLog, VscodeContext.DebugConsoleFile);
    }

    [Fact]
    public void AppendToUserMessageOmitsDebugConsoleUntilSomethingWasCaptured()
    {
        var debugLog = Path.Combine(_dir, "debug-console.log");
        File.WriteAllText(debugLog, "");
        UseContextFile(debugLog);

        var message = VscodeContext.AppendToUserMessage("hello");

        Assert.Equal("hello", message);
    }

    private void UseContextFile(string debugLog)
    {
        var contextFile = Path.Combine(_dir, "editor-context.json");
        File.WriteAllText(contextFile, JsonSerializer.Serialize(new { source = "vscode", debugConsoleFile = debugLog }));
        Environment.SetEnvironmentVariable("SIF_VSCODE_CONTEXT_FILE", contextFile);
    }
}
