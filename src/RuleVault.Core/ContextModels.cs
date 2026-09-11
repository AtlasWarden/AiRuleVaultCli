using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RuleVault.Core;

public enum TaskOperation
{
    Read,
    Edit,
    Test,
    Review,
    Release,
    MaintainVault
}

public enum ContextAudience
{
    Private,
    Shared
}

public enum RouteScope
{
    Global,
    Project,
    Subject
}

public enum LoadPolicy
{
    Always,
    Conditional,
    Historical
}

public sealed record TaskDescriptor(
    int SchemaVersion,
    string ProjectId,
    TaskOperation Operation,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> Paths,
    ContextAudience Audience,
    bool IncludeHistory,
    int OptionalBudgetChars,
    string? ProfileId)
{
    public const int CurrentSchemaVersion = 1;

    public static TaskDescriptor Create(
        string projectId,
        TaskOperation operation,
        IEnumerable<string>? subjects = null,
        IEnumerable<string>? paths = null,
        ContextAudience audience = ContextAudience.Private,
        bool includeHistory = false,
        int optionalBudgetChars = 8000,
        string? profileId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentOutOfRangeException.ThrowIfNegative(optionalBudgetChars);
        return new TaskDescriptor(
            CurrentSchemaVersion,
            projectId,
            operation,
            NormalizeSet(subjects, "subject"),
            NormalizePaths(paths),
            audience,
            includeHistory,
            optionalBudgetChars,
            string.IsNullOrWhiteSpace(profileId) ? null : profileId);
    }

    public string CanonicalJson()
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", SchemaVersion);
        writer.WriteString("project_id", ProjectId);
        writer.WriteString("operation", ToWireName(Operation));
        WriteArray(writer, "subjects", Subjects);
        WriteArray(writer, "paths", Paths);
        writer.WriteString("audience", ToWireName(Audience));
        writer.WriteBoolean("include_history", IncludeHistory);
        writer.WriteNumber("optional_budget_chars", OptionalBudgetChars);
        if (ProfileId is null)
        {
            writer.WriteNull("profile_id");
        }
        else
        {
            writer.WriteString("profile_id", ProfileId);
        }

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public string Fingerprint() => Sha256(CanonicalJson());

    public IReadOnlySet<TaskOperation> EffectiveOperations() => Operation switch
    {
        TaskOperation.Read => new HashSet<TaskOperation> { TaskOperation.Read },
        TaskOperation.Edit => new HashSet<TaskOperation> { TaskOperation.Read, TaskOperation.Edit },
        TaskOperation.Test => new HashSet<TaskOperation> { TaskOperation.Read, TaskOperation.Test },
        TaskOperation.Review => new HashSet<TaskOperation> { TaskOperation.Read, TaskOperation.Review },
        TaskOperation.Release => new HashSet<TaskOperation>
        {
            TaskOperation.Read, TaskOperation.Edit, TaskOperation.Test, TaskOperation.Review, TaskOperation.Release
        },
        TaskOperation.MaintainVault => new HashSet<TaskOperation> { TaskOperation.Read, TaskOperation.MaintainVault },
        _ => throw new ArgumentOutOfRangeException()
    };

    public static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    internal static string ToWireName(TaskOperation operation) => operation switch
    {
        TaskOperation.MaintainVault => "maintain-vault",
        _ => operation.ToString().ToLowerInvariant()
    };

    internal static string ToWireName(ContextAudience audience) => audience.ToString().ToLowerInvariant();

    private static void WriteArray(Utf8JsonWriter writer, string name, IEnumerable<string> values)
    {
        writer.WritePropertyName(name);
        writer.WriteStartArray();
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static IReadOnlyList<string> NormalizeSet(IEnumerable<string>? values, string name)
    {
        return (values ?? [])
            .Select(value => value?.Trim() ?? throw new ArgumentException($"{name} cannot be null."))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> NormalizePaths(IEnumerable<string>? paths)
    {
        var normalized = new List<string>();
        foreach (var input in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(input) || input.Contains('\\') || input.StartsWith('/') ||
                input.Contains('\0') || input.Split('/').Any(component => component is "." or ".." or ""))
            {
                throw new ArgumentException($"Path '{input}' is not a normalized repository-relative path.");
            }

            normalized.Add(input);
        }

        return normalized.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }
}

public sealed record ContextRoute(
    string RouteId,
    string FileId,
    string RelativePath,
    string Kind,
    RouteScope Scope,
    LoadPolicy LoadPolicy,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<TaskOperation> Operations,
    IReadOnlyList<string> PathPatterns,
    int OptionalPriority,
    IReadOnlyList<string> Requires,
    ContextAudience Audience);

public sealed record ContextDocument(ContextRoute Route, string Body, string CanonicalSha256);

public sealed record ContextDiagnostic(string Code, string Message, string? RouteId = null);

public sealed record ContextSegment(
    string RouteId,
    string FileId,
    string RelativePath,
    bool Mandatory,
    string Body,
    string CanonicalSha256);

public sealed record ContextPacket(
    string Body,
    string BodySha256,
    int Characters,
    int Utf8Bytes,
    int ApproximateTokens,
    IReadOnlyList<ContextSegment> Segments,
    IReadOnlyList<ContextDiagnostic> Diagnostics);

public sealed record ContextBuildResult(ContextPacket? Packet, IReadOnlyList<ContextDiagnostic> Diagnostics)
{
    public bool IsBlocked => Packet is null;
}
