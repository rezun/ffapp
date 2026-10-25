using System.Buffers;
using FFApp.Core.Models;

namespace FFApp.Core.Services;

/// <summary>An owned BGRA frame. Consumers must finish copying pixels before disposing it.</summary>
public sealed class ComparisonVideoFrame(int width, int height, double seconds, IMemoryOwner<byte> pixels,
    double pixelAspectRatio = 1) : IDisposable
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public double Seconds { get; } = seconds;
    public double PixelAspectRatio { get; } = pixelAspectRatio;
    public Memory<byte> Pixels => pixels.Memory[..checked(Width * Height * 4)];
    public void Dispose() => pixels.Dispose();
}

/// <summary>One persistent decoder, accessed serially on a worker thread.</summary>
public interface IComparisonDecoder : IDisposable
{
    /// <summary>One tick of the container's timestamp time base, in seconds.</summary>
    double TimestampResolutionSeconds => 0;
    ComparisonVideoFrame ReadAt(double seconds, CancellationToken ct = default);
    /// <summary>Accept timestamp rounding within the supplied uncertainty when matching a frame.</summary>
    ComparisonVideoFrame ReadAt(double seconds, double timestampToleranceSeconds, CancellationToken ct = default) =>
        ReadAt(seconds, ct);
    ComparisonVideoFrame? ReadNext(double? afterSeconds = null, CancellationToken ct = default);
}

public interface IComparisonDecoderFactory
{
    IComparisonDecoder Open(string path, CropRect? crop, CancellationToken ct = default);
}
