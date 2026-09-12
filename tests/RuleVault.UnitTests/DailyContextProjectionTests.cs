using RuleVault.Core;
using Xunit;

namespace RuleVault.UnitTests;

public sealed class DailyContextProjectionTests
{
    private const string Note = "# Today\n\n## Current Handoff\nKeep this blocker.\n\n## Sessions\n### Yesterday\nOLD_SESSION_DETAIL\n";

    [Fact]
    public void NormalContextKeepsHandoffAndExplicitDailyContextKeepsHistory()
    {
        var compact = DailyContextProjection.Render(Note, false);
        Assert.Contains("Keep this blocker.", compact, StringComparison.Ordinal);
        Assert.DoesNotContain("OLD_SESSION_DETAIL", compact, StringComparison.Ordinal);
        Assert.Equal(Note, DailyContextProjection.Render(Note, true));
    }

    [Theory]
    [InlineData("# Legacy note\nKeep everything.")]
    [InlineData(Note + "## Additional unresolved work\nMust remain.")]
    [InlineData("## Current Handoff\n```text\n## Sessions\nKeep fenced example.\n```\n")]
    [InlineData(Note + "```\nUnclosed fence")]
    [InlineData(Note + "## Sessions\nDuplicate ambiguous section")]
    public void UnknownOrAmbiguousLayoutsRemainComplete(string content) =>
        Assert.Equal(content, DailyContextProjection.Render(content, false));
}
