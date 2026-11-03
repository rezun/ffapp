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

    /// <summary>
    /// Samples frames across the whole source and returns a crop that removes black bars
    /// present in every sample, or <see langword="null"/> when no meaningful bars were found.
    /// </summary>
    Task<CropRect?> SuggestAsync(
        SourceMediaInfo mediaInfo,
        CancellationToken ct = default);
}
