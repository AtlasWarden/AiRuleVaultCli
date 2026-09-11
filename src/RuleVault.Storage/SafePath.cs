namespace RuleVault.Storage;

public enum SafePathProfile
{
    PrivateConfig,
    SharedAgents,
    PackageExtraction
}

public sealed record SafePathResult(bool Supported, string? FullPath, string? FailureCode, string? FailureReason)
{
    public static SafePathResult Ok(string path) => new(true, path, null, null);

    public static SafePathResult Unsupported(string code, string reason) => new(false, null, code, reason);
}

public static class SafePath
{
    public static SafePathResult ValidateRelative(
        string root,
        string relativePath,
        SafePathProfile profile,
        bool expectDirectory = false)
    {
        if (!Path.IsPathFullyQualified(root))
        {
            return SafePathResult.Unsupported("ROOT_NOT_ABSOLUTE", "The trusted root must be absolute.");
        }

        if (string.IsNullOrEmpty(relativePath) || relativePath.Contains('\0'))
        {
            return SafePathResult.Unsupported("PATH_INVALID", "The relative path is empty or contains NUL.");
        }

        if (relativePath.Contains('%') &&
            (relativePath.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
             relativePath.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
             relativePath.Contains("%00", StringComparison.OrdinalIgnoreCase)))
        {
            return SafePathResult.Unsupported("PATH_ENCODED_SEPARATOR", "Encoded separators/NUL are not accepted.");
        }

        if (relativePath.Contains(':') || relativePath.StartsWith('/') || relativePath.StartsWith('\\') ||
            Path.IsPathRooted(relativePath))
        {
            return SafePathResult.Unsupported("PATH_ROOTED", "Rooted, drive, UNC, device, or ADS paths are not accepted.");
        }

        var parts = relativePath.Split('/', StringSplitOptions.None);
        if (parts.Any(part => part.Length == 0 || part is "." or ".." || part.Contains('\\')))
        {
            return SafePathResult.Unsupported("PATH_TRAVERSAL", "Empty, dot, dot-dot, or alternate-separator components are not accepted.");
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative is "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return SafePathResult.Unsupported("PATH_ESCAPE", "The resolved path escapes the trusted root.");
        }

        if (profile == SafePathProfile.SharedAgents)
        {
            return SafePathResult.Unsupported("NATIVE_SHARED_ROOT_ADAPTER_REQUIRED", "Shared-agent access requires the phase-specific repository ingest adapter and is not available through generic storage.");
        }

        var componentCheck = CheckExistingComponents(fullRoot, fullPath, expectDirectory);
        if (componentCheck is not null)
        {
            return componentCheck;
        }

        return WindowsFileIdentity.CheckExistingFile(fullPath) ?? SafePathResult.Ok(fullPath);
    }

    private static SafePathResult? CheckExistingComponents(string root, string fullPath, bool expectDirectory)
    {
        var current = root;
        var relative = Path.GetRelativePath(root, fullPath);
        var parts = relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < parts.Length; index++)
        {
            current = Path.Combine(current, parts[index]);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                continue;
            }

            var attributes = File.GetAttributes(current);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return SafePathResult.Unsupported("PATH_REPARSE", $"Reparse or link indirection is not allowed: {relative}.");
            }

            if (index == parts.Length - 1 && expectDirectory && !Directory.Exists(current))
            {
                return SafePathResult.Unsupported("PATH_TYPE", "A directory was required.");
            }
        }

        return null;
    }
}
