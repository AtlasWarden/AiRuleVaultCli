using System.Diagnostics;
using RuleVault.Core;

namespace RuleVault.Agents;

public sealed record AgentRegistration(string AdapterId, string ExecutablePath, bool Approved, string ExecutableSha256);

public sealed record AgentLaunchRequest(
    AgentRegistration Registration,
    ContextPacket Packet,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> RequiredCapabilities);

public sealed record AgentLaunchResult(bool Started, int? ExitCode, string Code, string Detail);

public interface IAgentProcessRunner
{
    Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, string packetPath, CancellationToken cancellationToken);
}

public sealed class SupervisedLauncher
{
    private readonly IAgentProcessRunner _runner;

    public SupervisedLauncher(IAgentProcessRunner runner)
    {
        _runner = runner;
    }

    public async Task<AgentLaunchResult> LaunchAsync(AgentLaunchRequest request, string packetDirectory, CancellationToken cancellationToken = default)
    {
        if (!request.Registration.Approved || !Path.IsPathFullyQualified(request.Registration.ExecutablePath))
        {
            return new(false, null, "EXECUTABLE_NOT_APPROVED", "Agent executable must be registered by an explicit approved absolute path.");
        }

        if (request.RequiredCapabilities.Contains("os_filesystem_isolation", StringComparer.Ordinal))
        {
            return new(false, null, "REQUIRED_CAPABILITY_UNSUPPORTED", "Strict OS filesystem isolation is not implemented.");
        }

        if (request.RequiredCapabilities.Any(capability => capability != "process_tree_cleanup"))
        {
            return new(false, null, "REQUIRED_CAPABILITY_UNSUPPORTED", "Requested capability is not verified for this adapter.");
        }

        Directory.CreateDirectory(packetDirectory);
        var packetPath = Path.Combine(packetDirectory, $".packet-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(packetPath, request.Packet.Body, cancellationToken);
            var exitCode = await _runner.RunAsync(request.Registration.ExecutablePath, request.Arguments, packetPath, cancellationToken);
            return new(true, exitCode, exitCode == 0 ? "OK" : "CHILD_FAILED", exitCode == 0 ? "Child exited successfully." : "Child exited with failure.");
        }
        finally
        {
            if (File.Exists(packetPath))
            {
                File.Delete(packetPath);
            }
        }
    }
}

public sealed class ProcessAgentRunner : IAgentProcessRunner
{
    public async Task<int> RunAsync(string executablePath, IReadOnlyList<string> arguments, string packetPath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.ArgumentList.Add("--rule-vault-context-file");
        start.ArgumentList.Add(packetPath);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Agent process could not start.");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }
}
