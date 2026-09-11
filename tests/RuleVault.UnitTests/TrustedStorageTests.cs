using System.Text;
using RuleVault.Storage;
using Xunit;

namespace RuleVault.UnitTests;

public sealed class TrustedStorageTests
{
    [Fact]
    public void CanonicalTextHashIgnoresBomAndLineEndingOnly()
    {
        var expected = "911169ddaaf146aff539f58c26c489af3b892dff0fe283c1c264c65ae5aa59a2";

        Assert.Equal(expected, CanonicalHash.Text("a\r\nb\r"));
        Assert.Equal(expected, CanonicalHash.Text("\uFEFFa\nb\n"));
        Assert.NotEqual(CanonicalHash.Text("a\nb\n "), expected);
        Assert.Equal("9a3a45d01531a20e89ac6ae10b0b0beb0492acd7216a368aa062d1a5fecaf9cd", CanonicalHash.Raw("binary"u8));
    }

    [Fact]
    public void StrictJsonRejectsDuplicateKeysAtEveryDepth()
    {
        var duplicateRoot = Encoding.UTF8.GetBytes("{\"a\":1,\"a\":2}");
        var duplicateNested = Encoding.UTF8.GetBytes("{\"outer\":{\"a\":1,\"a\":2}}");

        var root = Assert.Throws<StorageFormatException>(() => StrictJson.Parse(duplicateRoot));
        var nested = Assert.Throws<StorageFormatException>(() => StrictJson.Parse(duplicateNested));

        Assert.Equal("JSON_DUPLICATE_KEY", root.Code);
        Assert.Equal("JSON_DUPLICATE_KEY", nested.Code);
    }

    [Fact]
    public void StrictJsonAcceptsUtf8BomWithoutRelaxingDuplicateChecks()
    {
        var bomJson = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"value\":1}")).ToArray();
        var bomDuplicate = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"value\":1,\"value\":2}")).ToArray();

        using var parsed = StrictJson.Parse(bomJson);
        Assert.Equal(1, parsed.RootElement.GetProperty("value").GetInt32());
        Assert.Equal("JSON_DUPLICATE_KEY", Assert.Throws<StorageFormatException>(() => StrictJson.Parse(bomDuplicate)).Code);
    }

    [Fact]
    public void StrictJsonRejectsExtensionsAndUnknownFields()
    {
        var comments = Encoding.UTF8.GetBytes("{\"a\":1 /* comment */}");
        var trailingComma = Encoding.UTF8.GetBytes("{\"a\":1,}");

        Assert.Equal("JSON_INVALID", Assert.Throws<StorageFormatException>(() => StrictJson.Parse(comments)).Code);
        Assert.Equal("JSON_INVALID", Assert.Throws<StorageFormatException>(() => StrictJson.Parse(trailingComma)).Code);
        Assert.Throws<StorageFormatException>(() => StrictJson.Deserialize<StrictRecord>(Encoding.UTF8.GetBytes("{\"A\":1,\"Unknown\":2}")));
    }

    [Fact]
    public void StrictYamlRejectsDuplicatesAliasesAndAmbiguousScalars()
    {
        var duplicate = "outer:\n  key: one\n  key: two\n";
        var alias = "base: &base value\ncopy: *base\n";
        var ambiguous = "created: 2026-09-10\n";

        Assert.Equal("YAML_DUPLICATE_KEY", Assert.Throws<StorageFormatException>(() => StrictYaml.ParseFrontmatter(duplicate)).Code);
        Assert.Equal("YAML_EXTENSION", Assert.Throws<StorageFormatException>(() => StrictYaml.ParseFrontmatter(alias)).Code);
        Assert.Equal("YAML_AMBIGUOUS_SCALAR", Assert.Throws<StorageFormatException>(() => StrictYaml.ParseFrontmatter(ambiguous)).Code);
    }

    [Fact]
    public void StrictYamlKeepsOnlyExplicitBooleanTyping()
    {
        var parsed = StrictYaml.ParseFrontmatter("hasInboundLinks: false\nstatus: \"active\"\naliases: []\n");

        Assert.False((bool)parsed["hasInboundLinks"]!);
        Assert.Equal("active", parsed["status"]);
        Assert.IsType<List<object?>>(parsed["aliases"]);
    }

    [Theory]
    [InlineData("../escape", "PATH_TRAVERSAL")]
    [InlineData("C:/escape", "PATH_ROOTED")]
    [InlineData("\\\\server\\share", "PATH_ROOTED")]
    [InlineData("safe/%2f/escape", "PATH_ENCODED_SEPARATOR")]
    [InlineData("safe/file:stream", "PATH_ROOTED")]
    [InlineData("safe/\0file", "PATH_INVALID")]
    public void SafePathRejectsHostileRelativePaths(string relative, string code)
    {
        var result = SafePath.ValidateRelative(Path.GetTempPath(), relative, SafePathProfile.PrivateConfig);

        Assert.False(result.Supported);
        Assert.Equal(code, result.FailureCode);
    }

    [Fact]
    public async Task StandardWriterUsesFreshRawHashPrecondition()
    {
        var root = CreateTempDirectory();
        try
        {
            var writer = new TrustedWriter();
            var created = await TrustedWriter.WriteTextAsync(root, "data.txt", "one", null, SafePathProfile.PrivateConfig, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "data.txt"), "external", TestContext.Current.CancellationToken);

            var conflict = await Assert.ThrowsAsync<WriteConflictException>(() =>
                TrustedWriter.WriteTextAsync(root, "data.txt", "two", created.RawSha256, SafePathProfile.PrivateConfig, TestContext.Current.CancellationToken));

            Assert.Contains("data.txt", conflict.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ProtectedLockAllowsOneOwnerAndReleasesSafely()
    {
        var root = CreateTempDirectory();
        try
        {
            var first = await ProtectedLock.AcquireAsync(root, "target.md", cancellationToken: TestContext.Current.CancellationToken);
            try
            {
                await Assert.ThrowsAsync<WriteConflictException>(() => ProtectedLock.AcquireAsync(root, "target.md", cancellationToken: TestContext.Current.CancellationToken));
            }
            finally
            {
                await first.DisposeAsync();
            }

            await using var second = await ProtectedLock.AcquireAsync(root, "target.md", cancellationToken: TestContext.Current.CancellationToken);
            await second.VerifyOwnershipAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task JournalRoundTripsThroughStrictJson()
    {
        var root = CreateTempDirectory();
        try
        {
            var id = Guid.NewGuid().ToString("D");
            var writer = Guid.NewGuid().ToString("D");
            var journal = new TransactionJournal(
                1,
                id,
                writer,
                TransactionState.Prepared,
                DateTimeOffset.UtcNow,
                [new TransactionTarget("a.txt", "MISSING", CanonicalHash.Raw("a"u8))]);

            await JournalStore.WriteAsync(root, journal, TestContext.Current.CancellationToken);
            var loaded = await JournalStore.ReadAsync(root, id, TestContext.Current.CancellationToken);

            Assert.Equal(TransactionState.Prepared, loaded.State);
            Assert.Equal("MISSING", loaded.Targets[0].BeforeSha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "RuleVaultCli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed record StrictRecord(int A);
}
