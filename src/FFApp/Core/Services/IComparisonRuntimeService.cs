namespace FFApp.Core.Services;

public sealed record ComparisonRuntimeStatus(bool IsAvailable, bool IsHdrToneMappingAvailable, string Message);

public interface IComparisonRuntimeService
{
    ComparisonRuntimeStatus Check(string ffmpegPath);
}
