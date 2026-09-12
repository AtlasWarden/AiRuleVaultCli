using System.Text.RegularExpressions;
using RuleVault.Storage;

namespace RuleVault.Agents;

public sealed record ManagedDocumentMetadata(
    string FileId,
    string Title,
    string Project,
    string Kind,
    string Status,
    string? LoadPolicy,
    string? Integrity)
{
    public bool RequiresIntegrityProtection =>
        Kind == "index" || LoadPolicy == "always" || Integrity == "protected";
}

public static partial class ManagedDocumentMetadataValidator
{
    private static readonly HashSet<string> RequiredKeys =
        ["file_id", "title", "project", "kind", "status", "aliases", "hasInboundLinks", "created"];

    private static readonly HashSet<string> AllowedKeys =
        [.. RequiredKeys, "updated", "runtime_canary_id", "load_policy", "integrity", "extensions"];

    private static readonly HashSet<string> Kinds = ["rule", "context", "index", "reference", "daily"];
    private static readonly HashSet<string> Statuses = ["active", "archived", "retired"];
    private static readonly HashSet<string> LoadPolicies = ["always", "conditional", "historical"];
    private static readonly HashSet<string> IntegrityClasses = ["normal", "protected"];

    public static ManagedDocumentMetadata Parse(string path, string content, bool requireCompleteSchema)
    {
        if (!content.StartsWith("---\n", StringComparison.Ordinal))
        {
            throw Invalid(path, "must begin with deterministic managed frontmatter");
        }

        var end = content.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (end < 0)
        {
            throw Invalid(path, "has no closing managed frontmatter delimiter");
        }

        IReadOnlyDictionary<string, object?> metadata;
        try
        {
            metadata = StrictYaml.ParseFrontmatter(content[4..end]);
        }
        catch (StorageFormatException exception)
        {
            throw new VaultAuthoringException("MANAGED_FRONTMATTER_INVALID", $"'{path}' has invalid deterministic frontmatter: {exception.Message}");
        }

        if (metadata.Keys.Any(key => !AllowedKeys.Contains(key)))
        {
            throw Invalid(path, "contains an unsupported frontmatter key");
        }

        var required = requireCompleteSchema ? RequiredKeys : RequiredKeys.Where(key => key is not ("project" or "created"));
        foreach (var key in required)
        {
            if (!metadata.ContainsKey(key))
            {
                throw Invalid(path, $"is missing frontmatter key '{key}'");
            }
        }

        var fileId = RequiredString(metadata, path, "file_id", 64);
        if (!Guid.TryParse(fileId, out _))
        {
            throw Invalid(path, "has no UUID file_id");
        }

        var title = RequiredString(metadata, path, "title", 256);
        var project = metadata.TryGetValue("project", out var projectValue) && projectValue is string projectText
            ? projectText
            : "global";
        if (!ProjectSlug().IsMatch(project))
        {
            throw Invalid(path, "has an invalid project slug");
        }

        var kind = RequiredString(metadata, path, "kind", 32);
        var status = RequiredString(metadata, path, "status", 32);
        if (!Kinds.Contains(kind) || !Statuses.Contains(status))
        {
            throw Invalid(path, "has an unsupported kind or status");
        }

        if (metadata["hasInboundLinks"] is not bool)
        {
            throw Invalid(path, "has an invalid hasInboundLinks value");
        }

        if (metadata["aliases"] is not List<object?> aliases || aliases.Count > 32 ||
            aliases.Any(alias => alias is not string value || value.Length > 256))
        {
            throw Invalid(path, "has an invalid aliases list");
        }

        if (requireCompleteSchema)
        {
            _ = RequiredString(metadata, path, "created", 128);
        }
        ValidateOptionalString(metadata, path, "updated", 128);
        ValidateOptionalMatch(metadata, path, "runtime_canary_id", RuntimeCanary());

        var loadPolicy = OptionalEnum(metadata, path, "load_policy", LoadPolicies);
        var integrity = OptionalEnum(metadata, path, "integrity", IntegrityClasses);
        if (metadata.TryGetValue("extensions", out var extensionsValue))
        {
            if (extensionsValue is not Dictionary<string, object?> extensions || extensions.Count > 32 || extensions.Keys.Any(key => !ExtensionKey().IsMatch(key)))
            {
                throw Invalid(path, "has an invalid extensions mapping");
            }
        }

        return new ManagedDocumentMetadata(fileId, title, project, kind, status, loadPolicy, integrity);
    }

    private static string RequiredString(IReadOnlyDictionary<string, object?> metadata, string path, string key, int maximum)
    {
        if (!metadata.TryGetValue(key, out var value) || value is not string text || string.IsNullOrWhiteSpace(text) || text.Length > maximum)
        {
            throw Invalid(path, $"has an invalid '{key}' value");
        }

        return text;
    }

    private static void ValidateOptionalString(IReadOnlyDictionary<string, object?> metadata, string path, string key, int maximum)
    {
        if (metadata.TryGetValue(key, out var value) && (value is not string text || string.IsNullOrWhiteSpace(text) || text.Length > maximum))
        {
            throw Invalid(path, $"has an invalid '{key}' value");
        }
    }

    private static void ValidateOptionalMatch(IReadOnlyDictionary<string, object?> metadata, string path, string key, Regex expression)
    {
        if (metadata.TryGetValue(key, out var value) && (value is not string text || !expression.IsMatch(text)))
        {
            throw Invalid(path, $"has an invalid '{key}' value");
        }
    }

    private static string? OptionalEnum(IReadOnlyDictionary<string, object?> metadata, string path, string key, IReadOnlySet<string> allowed)
    {
        if (!metadata.TryGetValue(key, out var value))
        {
            return null;
        }

        if (value is not string text || !allowed.Contains(text))
        {
            throw Invalid(path, $"has an invalid '{key}' value");
        }

        return text;
    }

    private static VaultAuthoringException Invalid(string path, string detail) =>
        new("MANAGED_FRONTMATTER_INVALID", $"'{path}' {detail}.");

    [GeneratedRegex("^(global|[a-z0-9]+(?:-[a-z0-9]+)*)$", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectSlug();

    [GeneratedRegex("^RV-CANARY-[A-Za-z0-9-]{4,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeCanary();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]*:[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionKey();
}
