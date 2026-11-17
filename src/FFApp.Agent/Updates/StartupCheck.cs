using FFApp.Agent.Storage;
using FFApp.Remote.Contracts;

namespace FFApp.Agent.Updates;

/// <summary>
/// Runs before every start (systemd <c>ExecStartPre</c>). After an update it counts start attempts;
/// when the new version failed to start twice, it switches <c>current</c> back to <c>previous</c>.
/// </summary>
public static class StartupCheck
{
    private static readonly int MaxStartAttempts = 2;

    public static int Run(AgentPaths paths, TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(output);
        var state = AtomicFile.ReadJson(paths.UpdateState, AgentJsonContext.Default.UpdateRecord);
        if (state is not { State: UpdateStateKind.Restarting })
            return 0;

        if (state.StartAttempts < MaxStartAttempts)
        {
            AtomicFile.WriteJson(paths.UpdateState, state with { StartAttempts = state.StartAttempts + 1 },
                AgentJsonContext.Default.UpdateRecord);
            output.WriteLine($"Starting updated agent {state.TargetVersion} (attempt {state.StartAttempts + 1}).");
            return 0;
        }

        if (new FileInfo(paths.Previous).LinkTarget is not { } previous)
        {
            output.WriteLine("The updated agent failed to start and no previous version is available.");
            return 0;
        }

        UpdateService.ReplaceSymlink(paths.Current, previous);
        var message = $"Version {state.TargetVersion} failed to start twice; restored {state.PreviousVersion}.";
        AtomicFile.WriteJson(paths.UpdateState,
            new UpdateRecord(UpdateStateKind.Failed, state.TargetVersion, state.PreviousVersion, Message: message),
            AgentJsonContext.Default.UpdateRecord);
        output.WriteLine(message);
        return 0;
    }
}
