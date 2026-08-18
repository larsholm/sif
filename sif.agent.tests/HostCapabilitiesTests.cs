using sif.agent.Services;
using Xunit;

namespace sif.agent.tests;

public sealed class HostCapabilitiesTests
{
    [Fact]
    public void BuildSummaryListsOnlyDetectedCapabilitiesInStableOrder()
    {
        var commands = new HashSet<string>(["python3", "node", "git", "huggingface-cli", "cargo", "clang"]);

        var summary = HostCapabilities.BuildSummary(commands.Contains);

        Assert.Equal("Host system has: Python, Node.js, git, hf, Rust, C/C++.", summary);
    }

    [Fact]
    public void BuildSummaryReturnsNullWhenNoKnownCapabilitiesArePresent()
    {
        Assert.Null(HostCapabilities.BuildSummary(_ => false));
    }
}
