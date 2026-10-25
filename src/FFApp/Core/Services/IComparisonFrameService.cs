using FFApp.Core.Models;

namespace FFApp.Core.Services;

public interface IComparisonFrameService
{
    Task<byte[]> DecodeAsync(string path, double seconds, CropRect? crop, CancellationToken ct = default);

    bool TryGetFrame(string path, double seconds, CropRect? crop, out byte[] frame)
    {
        frame = [];
        return false;
    }

    void Prefetch(string path, double seconds, double frameDuration, int count, CropRect? crop) { }
    void ClearCache() { }
}
