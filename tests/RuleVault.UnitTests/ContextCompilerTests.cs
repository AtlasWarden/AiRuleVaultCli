using System.Security.Cryptography;
using System.Text;
using RuleVault.Core;
using Xunit;

namespace RuleVault.UnitTests;

public sealed class ContextCompilerTests
{
    [Fact]
    public void Ctx001AndCtx002RenderingAndDescriptorAreDeterministic()
    {
        var documents = FixtureDocuments();
        var catalog = ContextCatalog.Compile(documents.Reverse());
        var left = TaskDescriptor.Create("project-a", TaskOperation.Edit, ["alpha", "beta"], ["src/a.cs", "src/b.cs"]);
        var right = TaskDescriptor.Create("project-a", TaskOperation.Edit, ["beta", "alpha"], ["src/b.cs", "src/a.cs"]);

        var leftPacket = catalog.Render(left).Packet!;
        var rightPacket = catalog.Render(right).Packet!;

        Assert.Equal(left.CanonicalJson(), right.CanonicalJson());
        Assert.Equal(left.Fingerprint(), right.Fingerprint());
        Assert.Equal(leftPacket.Body, rightPacket.Body);
        Assert.Equal(leftPacket.BodySha256, rightPacket.BodySha256);
    }

    [Fact]
    public void Ctx003IncludesMandatoryScopeAndExcludesUnrelatedSubjects()
    {
        var catalog = ContextCatalog.Compile(FixtureDocuments());
        var packet = catalog.Render(TaskDescriptor.Create("project-a", TaskOperation.Edit, ["alpha"], ["src/a.cs"])).Packet!;

        Assert.Collection(
            packet.Segments,
            segment => Assert.Equal("global-always", segment.RouteId),
            segment => Assert.Equal("project-always", segment.RouteId),
            segment => Assert.Equal("subject-alpha", segment.RouteId),
            segment => Assert.Equal("conditional-alpha", segment.RouteId));
        Assert.DoesNotContain(packet.Segments, segment => segment.RouteId == "subject-beta");
    }

    [Fact]
    public void Ctx004MandatoryOverflowBlocksWithoutPacket()
    {
        var catalog = ContextCatalog.Compile(FixtureDocuments());
        var result = catalog.Render(TaskDescriptor.Create("project-a", TaskOperation.Edit, ["alpha"]), maxTotalChars: 1);

        Assert.True(result.IsBlocked);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "MANDATORY_CONTEXT_EXCEEDS_BUDGET");
    }

    [Fact]
    public void Ctx005OptionalOverflowOmitsWholeSegmentDeterministically()
    {
        var catalog = ContextCatalog.Compile(FixtureDocuments());
        var descriptor = TaskDescriptor.Create("project-a", TaskOperation.Edit, ["alpha"], optionalBudgetChars: 5);

        var result = catalog.Render(descriptor);

        Assert.NotNull(result.Packet);
        Assert.DoesNotContain(result.Packet!.Segments, segment => segment.RouteId == "conditional-alpha");
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "OPTIONAL_CONTEXT_OMITTED" && diagnostic.RouteId == "conditional-alpha");
    }

    [Fact]
    public void Ctx006InvalidRoutesFailClosed()
    {
        var route = Route("a", RouteScope.Global, LoadPolicy.Always);
        var duplicate = Assert.Throws<ContextCatalogException>(() => ContextCatalog.Compile([Document(route), Document(route)]));
        Assert.Equal("ROUTE_DUPLICATE_ID", duplicate.Code);

        var missing = route with { Requires = ["missing"] };
        Assert.Equal(
            "ROUTE_MISSING_DEPENDENCY",
            Assert.Throws<ContextCatalogException>(() => ContextCatalog.Compile([Document(missing)])).Code);

        var first = route with { RouteId = "first", Requires = ["second"] };
        var second = route with { RouteId = "second", Requires = ["first"] };
        Assert.Equal(
            "ROUTE_CYCLE",
            Assert.Throws<ContextCatalogException>(() => ContextCatalog.Compile([Document(first), Document(second)])).Code);

        var invalidPattern = route with { PathPatterns = ["src/[bad]"] };
        Assert.Equal(
            "ROUTE_PATTERN_INVALID",
            Assert.Throws<ContextCatalogException>(() => ContextCatalog.Compile([Document(invalidPattern)])).Code);
    }

    [Fact]
    public void Ctx007PrivateMandatoryRuleBlocksSharedAudience()
    {
        var privateRule = Route("private", RouteScope.Global, LoadPolicy.Always) with { Audience = ContextAudience.Private };
        var catalog = ContextCatalog.Compile([Document(privateRule)]);

        var result = catalog.Render(TaskDescriptor.Create("project-a", TaskOperation.Read, audience: ContextAudience.Shared));

        Assert.True(result.IsBlocked);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "AUDIENCE_CONFLICT");
    }

    [Fact]
    public void Ctx008ContextRenderingIsReadOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var marker = Path.Combine(root, "marker.txt");
        try
        {
            File.WriteAllText(marker, "unchanged");
            var before = File.GetLastWriteTimeUtc(marker);
            var catalog = ContextCatalog.Compile(FixtureDocuments());

            _ = catalog.Render(TaskDescriptor.Create("project-a", TaskOperation.Read, ["alpha"]));

            Assert.Equal("unchanged", File.ReadAllText(marker));
            Assert.Equal(before, File.GetLastWriteTimeUtc(marker));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("read", TaskOperation.Read)]
    [InlineData("edit", TaskOperation.Edit)]
    [InlineData("test", TaskOperation.Test)]
    [InlineData("review", TaskOperation.Review)]
    [InlineData("release", TaskOperation.Release)]
    [InlineData("maintain-vault", TaskOperation.MaintainVault)]
    public void Ctx012SixFixtureDescriptorsHaveMeasuredProceduralContracts(string name, TaskOperation operation)
    {
        var catalog = ContextCatalog.Compile(FixtureDocuments());
        var packet = catalog.Render(TaskDescriptor.Create("project-a", operation, ["alpha"])).Packet!;

        Assert.True(Encoding.UTF8.GetByteCount(packet.Body.Split("\n\n", 2)[0]) <= 4000, name);
        Assert.Equal((int)Math.Ceiling(packet.Characters / 4d), packet.ApproximateTokens);
    }

    [Fact]
    public void RequiredSubjectDependenciesCannotBeDroppedByZeroOptionalBudget()
    {
        var safety = Route("safety", RouteScope.Subject, LoadPolicy.Always, ["memory"]);
        var daily = Route("daily", RouteScope.Subject, LoadPolicy.Always, ["daily"]) with { Requires = ["safety"] };
        var catalog = ContextCatalog.Compile([Document(safety, "Required safety text"), Document(daily)]);
        var packet = catalog.Render(TaskDescriptor.Create("p", TaskOperation.Edit, ["daily"], optionalBudgetChars: 0)).Packet!;
        Assert.Contains(packet.Segments, segment => segment.RouteId == "safety" && segment.Mandatory);
        Assert.Contains("Required safety text", packet.Body, StringComparison.Ordinal);
        Assert.True(catalog.Render(TaskDescriptor.Create("p", TaskOperation.Edit, ["daily"]), maxTotalChars: 1).IsBlocked);
    }

    [Fact]
    public void ContextJsonCarriesOneBodyAndSegmentProvenance()
    {
        var packet = ContextCatalog.Compile([Document(Route("one", RouteScope.Global, LoadPolicy.Always), "UNIQUE_RULE_TEXT")])
            .Render(TaskDescriptor.Create("p", TaskOperation.Read)).Packet!;
        var serialized = System.Text.Json.JsonSerializer.Serialize(packet);
        Assert.Equal(2, serialized.Split("UNIQUE_RULE_TEXT", StringSplitOptions.None).Length);
        Assert.Contains("UNIQUE_RULE_TEXT", packet.Segments[0].Body, StringComparison.Ordinal);
    }

    private static IReadOnlyList<ContextDocument> FixtureDocuments() =>
    [
        Document(Route("global-always", RouteScope.Global, LoadPolicy.Always), "global body"),
        Document(Route("project-always", RouteScope.Project, LoadPolicy.Always), "project body"),
        Document(Route("subject-alpha", RouteScope.Subject, LoadPolicy.Always, ["alpha"]) with { PathPatterns = ["src/a.cs"] }, "alpha body"),
        Document(Route("subject-beta", RouteScope.Subject, LoadPolicy.Always, ["beta"]) with { PathPatterns = ["src/b.cs"] }, "beta body"),
        Document(Route("conditional-alpha", RouteScope.Subject, LoadPolicy.Conditional, ["alpha"], priority: 10), "optional alpha detail")
    ];

    private static ContextRoute Route(
        string id,
        RouteScope scope,
        LoadPolicy policy,
        IReadOnlyList<string>? subjects = null,
        int priority = 0) =>
        new(
            id,
            $"file-{id}",
            $"context/{id}.md",
            "rule",
            scope,
            policy,
            subjects ?? [],
            [TaskOperation.Read, TaskOperation.Edit, TaskOperation.Test, TaskOperation.Review, TaskOperation.Release, TaskOperation.MaintainVault],
            ["src/**"],
            priority,
            [],
            ContextAudience.Private);

    private static ContextDocument Document(ContextRoute route, string? body = null)
    {
        var text = body ?? route.RouteId;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        return new ContextDocument(route, text, hash);
    }
}
