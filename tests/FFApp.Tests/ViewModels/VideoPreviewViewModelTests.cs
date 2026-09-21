using System.Collections.Immutable;

using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class VideoPreviewViewModelTests
{
    [Fact]
    public async Task DetectCropAsync_AppliesDetectedCropAtCurrentPreviewPosition()
    {
        var detector = new StubCropDetectionService(new CropRect(0, 140, 1920, 800));
        var sut = new VideoPreviewViewModel(null, detector);
        sut.LoadMedia(CreateMediaInfo());
        sut.HasMedia = true;
        sut.TimeMs = 45_678;

        await sut.DetectCropCommand.ExecuteAsync(null);

        detector.Position.Should().Be(TimeSpan.FromMilliseconds(45_678));
        sut.CropX.Should().Be(0);
        sut.CropY.Should().Be(140);
        sut.CropW.Should().Be(1920);
        sut.CropH.Should().Be(800);
        sut.IsCropDirty.Should().BeTrue();
        sut.CropDetectionStatus.Should().Be("Detected 1920 x 800 at 0, 140");
    }

    private static SourceMediaInfo CreateMediaInfo() => new()
    {
        SourcePath = "/videos/movie.mkv",
        DurationSeconds = 7200,
        VideoStreams = ImmutableArray.Create(new VideoStreamInfo(0, Width: 1920, Height: 1080)),
    };

    private sealed class StubCropDetectionService(CropRect result) : ICropDetectionService
    {
        public TimeSpan? Position { get; private set; }

        public Task<CropRect> DetectAsync(
            SourceMediaInfo mediaInfo,
            TimeSpan position,
            CancellationToken ct = default)
        {
            Position = position;
            return Task.FromResult(result);
        }
    }
}
