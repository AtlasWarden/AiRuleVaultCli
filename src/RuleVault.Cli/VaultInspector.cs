using System.Text.Json;
using RuleVault.Core;
using RuleVault.Storage;

namespace RuleVault.Cli;

internal sealed record VaultInspection(
    string VaultId,
    string VaultRoot,
    string IndexCanonicalSha256,
    int IndexCharacters,
    bool RouteCatalogPresent,
    string ContentIntegrityCanonicalSha256);

internal static class VaultInspector
{
    public static async Task<VaultInspection> InspectAsync(string vaultRoot, CancellationToken cancellationToken = default)
    {
        var metadataBytes = await TrustedFileSystem.ReadAllBytesAsync(vaultRoot, ".vault-system/vault.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var metadata = StrictJson.Parse(metadataBytes);
        if (!metadata.RootElement.TryGetProperty("vault_id", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString()))
        {
            throw new StorageFormatException("VAULT_METADATA_INVALID", "vault.json must contain a non-empty vault_id.");
        }

        var index = await TrustedFileSystem.ReadTextAsync(vaultRoot, "index.md", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var integrity = await TrustedFileSystem.ReadTextAsync(vaultRoot, ".vault-system/content-integrity.json", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        using var integrityDocument = StrictJson.Parse(System.Text.Encoding.UTF8.GetBytes(integrity));
        if (!integrityDocument.RootElement.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            throw new StorageFormatException("INTEGRITY_MANIFEST_INVALID", "Content integrity manifest has no files array.");
        }

        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in files.EnumerateArray())
        {
            if (!entry.TryGetProperty("path", out var entryPath) || entryPath.ValueKind != JsonValueKind.String ||
                !entry.TryGetProperty("sha256", out var expectedHash) || expectedHash.ValueKind != JsonValueKind.String ||
                !paths.Add(entryPath.GetString()!))
            {
                throw new StorageFormatException("INTEGRITY_MANIFEST_INVALID", "Content integrity manifest has an invalid or duplicate entry.");
            }

            var content = await TrustedFileSystem.ReadTextAsync(vaultRoot, entryPath.GetString()!, SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
            if (!string.Equals(CanonicalHash.Text(content), expectedHash.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                throw new StorageFormatException("INTEGRITY_HASH_MISMATCH", $"Protected content does not match manifest: '{entryPath.GetString()}'.");
            }
        }
        var routes = SafePath.ValidateRelative(vaultRoot, ".vault-system/routes.json", SafePathProfile.PrivateConfig);
        return new VaultInspection(
            id.GetString()!,
            Path.GetFullPath(vaultRoot),
            CanonicalHash.Text(index),
            index.Length,
            routes.Supported && File.Exists(routes.FullPath!),
            CanonicalHash.Text(integrity));
    }

    public static async Task<ContextPacket> BuildLegacyIndexPacketAsync(string vaultRoot, CancellationToken cancellationToken = default)
    {
        var inspection = await InspectAsync(vaultRoot, cancellationToken);
        var index = await TrustedFileSystem.ReadTextAsync(vaultRoot, "index.md", SafePathProfile.PrivateConfig, cancellationToken: cancellationToken);
        var segment = new ContextSegment("legacy-index", "vault-index", "index.md", true, index, inspection.IndexCanonicalSha256);
        return new ContextPacket(index, inspection.IndexCanonicalSha256, index.Length, System.Text.Encoding.UTF8.GetByteCount(index), (index.Length + 3) / 4, [segment],
            inspection.RouteCatalogPresent ? [] : [new ContextDiagnostic("ROUTE_CATALOG_ABSENT", "Legacy vault: index packet is available, but catalog routing is not installed.")]);
    }
}
