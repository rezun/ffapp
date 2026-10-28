using FFRux.Core.Models;

namespace FFRux.Core;

public enum RemuxOutcome
{
    Completed,
    Failed,
    Canceled,
}

/// <param name="Error">Why the job failed, in a sentence or two plus the last FFmpeg lines.</param>
/// <param name="Warnings">Problems that did not stop the job, such as a failed mkvpropedit run.</param>
public sealed record RemuxResult(
    RemuxOutcome Outcome,
    string OutputPath,
    TimeSpan Elapsed,
    string? Error = null,
    long? OutputBytes = null)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public enum RemuxStage
{
    Testing,
    Remuxing,
    PostProcessing,
}

/// <param name="Percentage">0 to 100 while remuxing, null when unknown.</param>
public sealed record RemuxProgress(RemuxStage Stage, double? Percentage, TimeSpan Elapsed);

public interface IRemuxJobRunner
{
    /// <summary>
    /// Runs a test remux, the remux into a hidden partial file, mkvpropedit, and finally replaces the
    /// output with the partial file. Cancelling at any point deletes the partial file and leaves an
    /// existing output file untouched.
    /// </summary>
    Task<RemuxResult> RunAsync(
        RemuxPlan plan,
        Action<RemuxProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default);
}
