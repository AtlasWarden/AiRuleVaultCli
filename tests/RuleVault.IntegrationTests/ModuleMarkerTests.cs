using Xunit;

namespace RuleVault.IntegrationTests;

public sealed class ModuleMarkerTests
{
    [Fact]
    public void InstallationMarkerIdentifiesPhase00()
    {
        Assert.Equal("00", RuleVault.Installation.ModuleMarker.Phase);
    }
}
