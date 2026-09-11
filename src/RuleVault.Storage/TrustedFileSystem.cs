using System.Security;
using System.Text;

namespace RuleVault.Storage;

public sealed class TrustedFileSystem
{
    public static async Task<byte[]> ReadAllBytesAsync(
        string root,
        string relativePath,
        SafePathProfile profile,
        StorageLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        limits ??= new StorageLimits();
        var path = SafePath.ValidateRelative(root, relativePath, profile);
        EnsureSupported(path);
        try
        {
            await using var stream = WindowsFileIdentity.OpenBeneathRoot(root, relativePath, FileAccess.Read);
            WindowsFileIdentity.EnsureSafeHandle(stream.SafeFileHandle, relativePath);
            if (stream.Length > limits.MaxManagedTextBytes)
            {
                throw new StorageFormatException("FILE_TOO_LARGE", $"File exceeds {limits.MaxManagedTextBytes} bytes.");
            }

            using var memory = new MemoryStream(capacity: checked((int)stream.Length));
            await stream.CopyToAsync(memory, cancellationToken);
            return memory.ToArray();
        }
        catch (FileNotFoundException)
        {
            throw new FileNotFoundException("Trusted target does not exist.", relativePath);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SecurityException($"Access denied for trusted target '{relativePath}'.", exception);
        }
    }

    public static async Task<string> ReadTextAsync(
        string root,
        string relativePath,
        SafePathProfile profile,
        StorageLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await ReadAllBytesAsync(root, relativePath, profile, limits, cancellationToken);
        try
        {
            return CanonicalHash.DecodeUtf8(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new StorageFormatException("UTF8_INVALID", "Trusted text is not valid UTF-8.", exception);
        }
    }

    public static void EnsureSupported(SafePathResult result)
    {
        if (!result.Supported)
        {
            throw new SafePathException(result.FailureCode!, result.FailureReason!);
        }
    }
}

public sealed class SafePathException : Exception
{
    public SafePathException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
