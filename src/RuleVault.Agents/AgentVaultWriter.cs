using RuleVault.Storage;

namespace RuleVault.Agents;

public sealed record AgentVaultWriteRequest(
    string VaultRoot,
    string AdapterId,
    string RelativePath,
    string Content,
    string? ExpectedRawSha256,
    string? ConfigRoot = null);

public sealed record AgentVaultWriteResult(string RelativePath, string RawSha256, bool Created);

/// <summary>Compatibility entry point for adapter-originated managed vault writes.</summary>
public static class AgentVaultWriter
{
    public static async Task<AgentVaultWriteResult> WriteAsync(
        AgentVaultWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!IsAdapterId(request.AdapterId))
        {
            throw new AgentVaultWriteException("ADAPTER_ID_INVALID", "Adapter ID must be lowercase letters, digits, or hyphens.");
        }

        try
        {
            var write = await VaultAuthoring.WriteAsync(new VaultWriteRequest(
                request.VaultRoot, request.ConfigRoot, request.RelativePath, request.Content, request.ExpectedRawSha256), cancellationToken);
            return new AgentVaultWriteResult(write.RelativePath, write.RawSha256, write.Created);
        }
        catch (VaultAuthoringException exception)
        {
            throw new AgentVaultWriteException(exception.Code, exception.Message);
        }
    }

    private static bool IsAdapterId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(character =>
            character is >= 'a' and <= 'z' || character is >= '0' and <= '9' || character == '-');
}

public sealed class AgentVaultWriteException : Exception
{
    public AgentVaultWriteException(string code, string message) : base(message) => Code = code;

    public string Code { get; }
}
