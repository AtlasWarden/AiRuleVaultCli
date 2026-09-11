using Xunit;

namespace RuleVault.UnitTests;

public sealed class ModuleMarkerTests
{
    [Fact]
    public void CoreMarkerIdentifiesPhase00()
    {
        Assert.Equal("00", RuleVault.Core.ModuleMarker.Phase);
    }
}
