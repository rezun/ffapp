using FFApp.Core.Models;

namespace FFApp.Core.Services;

public sealed class CropDetectionException(string message, Exception? inner = null)
    : Exception(message, inner);

public interface ICropDetectionService
{
    Task<CropRect> DetectAsync(
        SourceMediaInfo mediaInfo,
        TimeSpan position,
        CancellationToken ct = default);
}
