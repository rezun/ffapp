using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface ICommandBuilder
{
    /// <summary>
    /// Builds the commands for <paramref name="job"/>. Temporary files (extracted images,
    /// two-pass logs) go to <paramref name="workDirectory"/> when given; otherwise images go to
    /// the system temp directory and two-pass logs next to the output file.
    /// </summary>
    IReadOnlyList<GeneratedCommand> Build(JobDefinition job, SourceMediaInfo mediaInfo, string? workDirectory = null);
}
