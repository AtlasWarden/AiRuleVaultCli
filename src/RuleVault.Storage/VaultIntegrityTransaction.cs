namespace RuleVault.Storage;

/// <summary>
/// Serializes protected-content writers while allowing readers to take
/// optimistic snapshots without acquiring a shared lock.
/// </summary>
public static class VaultIntegrityTransaction
{
    public const string WriterTarget = "__rule-vault-protected-content-transaction__";
    public static readonly TimeSpan WriterWaitTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ReaderWaitTimeout = TimeSpan.FromSeconds(10);

    public static string StateRoot(string vaultRoot) => Path.Combine(Path.GetFullPath(vaultRoot), ".vault-system");

    public static Task<ProtectedLock> AcquireWriterAsync(string vaultRoot, CancellationToken cancellationToken = default) =>
        ProtectedLock.AcquireAsync(
            StateRoot(vaultRoot),
            WriterTarget,
            waitTimeout: WriterWaitTimeout,
            cancellationToken: cancellationToken);

    public static bool IsWriterActive(string vaultRoot) =>
        ProtectedLock.IsHeld(StateRoot(vaultRoot), WriterTarget);

    public static async Task WaitForWriterAsync(string vaultRoot, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        while (IsWriterActive(vaultRoot))
        {
            if (DateTimeOffset.UtcNow - started >= ReaderWaitTimeout)
            {
                throw new WriteConflictException("A protected Rule Vault update is still in progress. Retry after that update completes.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(5, 21)), cancellationToken);
        }
    }
}
