using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface ICommandBuilder
{
    IReadOnlyList<GeneratedCommand> Build(JobDefinition job, SourceMediaInfo mediaInfo);
}
