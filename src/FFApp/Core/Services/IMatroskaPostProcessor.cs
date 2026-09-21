using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IMatroskaPostProcessor
{
    GeneratedCommand? BuildCommand(JobDefinition job);
}
