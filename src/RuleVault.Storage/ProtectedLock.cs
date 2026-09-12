using System.Text;
using System.Text.Json;

namespace RuleVault.Storage;

public sealed record LockOwner(
    string WriterId,
    string Target,
    DateTimeOffset AcquiredUtc,
    DateTimeOffset HeartbeatUtc,
    int LeaseSeconds);

public sealed class ProtectedLock : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _lockPath;
    private readonly LockOwner _owner;
    private bool _released;

    private ProtectedLock(string lockPath, LockOwner owner)
    {
        _lockPath = lockPath;
        _owner = owner;
    }

    public string WriterId => _owner.WriterId;

    public static async Task<ProtectedLock> AcquireAsync(
        string stateRoot,
        string target,
        int leaseSeconds = 900,
        TimeSpan? waitTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(leaseSeconds);
        if (waitTimeout is { } timeout && timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(waitTimeout));
        }

        var lockPath = GetLockPath(stateRoot, target);
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        var now = DateTimeOffset.UtcNow;
        var owner = new LockOwner(Guid.NewGuid().ToString("D"), target, now, now, leaseSeconds);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(owner, JsonOptions));
        var started = DateTimeOffset.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var created = false;
            try
            {
                await using var stream = new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
                created = true;
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                return new ProtectedLock(lockPath, owner);
            }
            catch (IOException ex) when (File.Exists(lockPath))
            {
                if (waitTimeout is null || DateTimeOffset.UtcNow - started >= waitTimeout.Value)
                {
                    throw new WriteConflictException($"Protected lock is busy for '{target}'.", ex);
                }

                var remaining = waitTimeout.Value - (DateTimeOffset.UtcNow - started);
                var delay = TimeSpan.FromMilliseconds(Math.Min(Random.Shared.Next(8, 26), Math.Max(1, remaining.TotalMilliseconds)));
                await Task.Delay(delay, cancellationToken);
            }
            catch
            {
                if (created)
                {
                    TryDelete(lockPath);
                }
                throw;
            }
        }
    }

    public static bool IsHeld(string stateRoot, string target) => File.Exists(GetLockPath(stateRoot, target));

    public async Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotReleased();
        var current = await ReadOwnerAsync(cancellationToken);
        if (current.WriterId != _owner.WriterId)
        {
            throw new InvalidOperationException("Protected lock ownership changed.");
        }

        await ReplaceOwnerAsync(current with { HeartbeatUtc = DateTimeOffset.UtcNow }, cancellationToken);
    }

    public async Task VerifyOwnershipAsync(CancellationToken cancellationToken = default)
    {
        EnsureNotReleased();
        var current = await ReadOwnerAsync(cancellationToken);
        if (current.WriterId != _owner.WriterId || !File.Exists(_lockPath))
        {
            throw new InvalidOperationException("Protected lock ownership changed.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        try
        {
            var current = await ReadOwnerAsync(CancellationToken.None);
            if (current.WriterId == _owner.WriterId)
            {
                TryDelete(_lockPath);
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    public static async Task<string?> QuarantineIfStaleAsync(
        string stateRoot,
        string target,
        TimeSpan threshold,
        CancellationToken cancellationToken = default)
    {
        var lockId = CanonicalHash.Raw(Encoding.UTF8.GetBytes(target));
        var lockPath = GetLockPath(stateRoot, target);
        if (!File.Exists(lockPath))
        {
            return null;
        }

        var owner = await ReadOwnerFileAsync(lockPath, cancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        var reread = await ReadOwnerFileAsync(lockPath, cancellationToken);
        if (reread.WriterId != owner.WriterId || reread.HeartbeatUtc != owner.HeartbeatUtc ||
            DateTimeOffset.UtcNow - reread.HeartbeatUtc < threshold)
        {
            return null;
        }

        var quarantineRoot = Path.Combine(stateRoot, "locks", "stale");
        Directory.CreateDirectory(quarantineRoot);
        var quarantine = Path.Combine(quarantineRoot, $"{lockId}.{Guid.NewGuid():N}.lock");
        File.Move(lockPath, quarantine);
        return quarantine;
    }

    private async Task<LockOwner> ReadOwnerAsync(CancellationToken cancellationToken) =>
        await ReadOwnerFileAsync(_lockPath, cancellationToken);

    private async Task ReplaceOwnerAsync(LockOwner owner, CancellationToken cancellationToken)
    {
        var temp = _lockPath + $".{Guid.NewGuid():N}.stage";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(owner, JsonOptions), cancellationToken);
            File.Move(temp, _lockPath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static async Task<LockOwner> ReadOwnerFileAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return StrictJson.Deserialize<LockOwner>(bytes);
    }

    private static string GetLockPath(string stateRoot, string target)
    {
        var lockId = CanonicalHash.Raw(Encoding.UTF8.GetBytes(target));
        return Path.Combine(stateRoot, "locks", $"{lockId}.lock");
    }

    private void EnsureNotReleased()
    {
        ObjectDisposedException.ThrowIf(_released, this);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (FileNotFoundException)
        {
        }
    }
}
