using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Infrastructure;

public sealed class MatroskaPostProcessor(
    ExecutablePathsConfig pathsConfig) : IMatroskaPostProcessor
{
    public GeneratedCommand? BuildCommand(JobDefinition job)
    {
        if (!job.PostProcessing.RecalculateMatroskaStatistics
            || job.Container is not (OutputContainer.Mkv
                or OutputContainer.Mka
                or OutputContainer.Webm))
        {
            return null;
        }

        if (pathsConfig.MkvpropeditPath is not { } executablePath)
        {
            throw new CommandBuildException(
                "Matroska statistics were requested, but mkvpropedit is not available.");
        }

        return DefaultCommandBuilder.CreateGeneratedCommand(
            executablePath,
            [
                job.OutputPath,
                "--delete-track-statistics-tags",
                "--add-track-statistics-tags",
            ],
            ProcessProtocol.Plain);
    }
}
