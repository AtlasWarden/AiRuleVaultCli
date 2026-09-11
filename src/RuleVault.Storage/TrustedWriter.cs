using System.Security.Cryptography;
using System.Text;

namespace RuleVault.Storage;

public sealed record WriteResult(string RelativePath, string RawSha256, bool Created);

public sealed class WriteConflictException : Exception
{
    public WriteConflictException(string message)
        : base(message)
    {
    }

    public WriteConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class TrustedWriter
{
    public static async Task<WriteResult> WriteTextAsync(
        string root,
        string relativePath,
        string content,
        string? expectedRawSha256,
        SafePathProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var path = SafePath.ValidateRelative(root, relativePath, profile);
        TrustedFileSystem.EnsureSupported(path);
        var target = path.FullPath!;
        var directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        var exists = File.Exists(target);
        var currentHash = exists ? await RawFileHashAsync(target, cancellationToken) : null;
        if (!string.Equals(currentHash, expectedRawSha256, StringComparison.OrdinalIgnoreCase))
        {
            if (exists || expectedRawSha256 is not null)
            {
                throw new WriteConflictException($"Write precondition failed for '{relativePath}'.");
            }
        }

        var stage = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.stage");
        try
        {
            await File.WriteAllTextAsync(stage, content, new UTF8Encoding(false), cancellationToken);
            if (exists)
            {
                File.Move(stage, target, overwrite: true);
            }
            else
            {
                File.Move(stage, target);
            }

            var afterHash = await RawFileHashAsync(target, cancellationToken);
            return new WriteResult(relativePath, afterHash, !exists);
        }
        finally
        {
            if (File.Exists(stage))
            {
                File.Delete(stage);
            }
        }
    }

    private static async Task<string> RawFileHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = WindowsFileIdentity.Open(path, FileAccess.Read);
        WindowsFileIdentity.EnsureSafeHandle(stream.SafeFileHandle, path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }
}
