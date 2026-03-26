using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        GeneratedCommand command,
        double? expectedDurationSeconds = null,
        int? expectedFrameCount = null,
        Action<ProcessProgress>? progressCallback = null,
        Action<string>? logCallback = null,
        CancellationToken ct = default);
}
