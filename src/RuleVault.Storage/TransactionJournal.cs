using System.Text.Json;

namespace RuleVault.Storage;

public enum TransactionState
{
    Planned,
    Staging,
    Prepared,
    Committing,
    Committed,
    Verified,
    Recoverable,
    Conflicted,
    RolledBack
}

public sealed record TransactionTarget(string Path, string BeforeSha256, string StagedSha256);

public sealed record TransactionJournal(
    int SchemaVersion,
    string TransactionId,
    string WriterId,
    TransactionState State,
    DateTimeOffset CreatedUtc,
    IReadOnlyList<TransactionTarget> Targets);

public sealed class JournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task WriteAsync(string stateRoot, TransactionJournal journal, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(stateRoot);
        var path = GetPath(stateRoot, journal.TransactionId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(journal, JsonOptions);
        var stage = path + $".{Guid.NewGuid():N}.stage";
        try
        {
            await File.WriteAllTextAsync(stage, json, cancellationToken);
            File.Move(stage, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(stage))
            {
                File.Delete(stage);
            }
        }
    }

    public static async Task<TransactionJournal> ReadAsync(
        string stateRoot,
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        var path = GetPath(stateRoot, transactionId);
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return StrictJson.Deserialize<TransactionJournal>(bytes);
    }

    public static string GetPath(string stateRoot, string transactionId)
    {
        if (!Guid.TryParseExact(transactionId, "D", out _))
        {
            throw new SafePathException("TRANSACTION_ID", "Transaction ID must be a canonical UUID.");
        }

        return Path.Combine(stateRoot, "transactions", $"{transactionId}.json");
    }
}
