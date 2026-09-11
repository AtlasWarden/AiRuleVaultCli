namespace RuleVault.Storage;

public sealed record StorageLimits(
    int MaxJsonBytes = 1 * 1024 * 1024,
    int MaxYamlBytes = 1 * 1024 * 1024,
    int MaxManagedTextBytes = 4 * 1024 * 1024,
    int MaxDepth = 32,
    int MaxStringLength = 64 * 1024,
    int MaxCollectionItems = 50_000);
