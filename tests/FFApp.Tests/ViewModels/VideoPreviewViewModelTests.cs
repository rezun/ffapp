using System.Collections.Immutable;

using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class VideoPreviewViewModelTests
{
    [Fact]
    public void Audio_DefaultsToFullVolumeAndUnmuted()
    {
        using var sut = new VideoPreviewViewModel(null);

        sut.Volume.Should().Be(100);
        sut.IsMuted.Should().BeFalse();
        sut.MuteLabel.Should().Be("Mute");
    }

    [Fact]
    public void ToggleMute_PreservesVolumeAndUpdatesLabel()
    {
        using var sut = new VideoPreviewViewModel(null) { Volume = 42 };

        sut.ToggleMuteCommand.Execute(null);

        sut.IsMuted.Should().BeTrue();
        sut.MuteLabel.Should().Be("Unmute");
        sut.Volume.Should().Be(42);

        sut.ToggleMuteCommand.Execute(null);

        sut.IsMuted.Should().BeFalse();
        sut.MuteLabel.Should().Be("Mute");
        sut.Volume.Should().Be(42);
    }

    [Theory]
    [InlineData(-10, 0, true)]
    [InlineData(0, 0, true)]
    [InlineData(35, 35, false)]
    [InlineData(150, 100, false)]
    public void Volume_ClampsToPreviewRange(int volume, int expected, bool muted)
    {
        using var sut = new VideoPreviewViewModel(null);

        sut.Volume = volume;

        sut.Volume.Should().Be(expected);
        sut.IsMuted.Should().Be(muted);
    }

    [Fact]
    public void RaisingVolume_UnmutesAudio()
    {
        using var sut = new VideoPreviewViewModel(null) { Volume = 42 };
        sut.ToggleMuteCommand.Execute(null);

        sut.Volume = 50;

        sut.IsMuted.Should().BeFalse();
        sut.Volume.Should().Be(50);
    }

    [Fact]
    public void UnmuteAtZero_RestoresLastAudibleVolume()
    {
        using var sut = new VideoPreviewViewModel(null) { Volume = 42 };
        sut.Volume = 0;

        sut.ToggleMuteCommand.Execute(null);

        sut.Volume.Should().Be(42);
        sut.IsMuted.Should().BeFalse();
    }

    [Fact]
    public void LoadingSource_PreservesPreviewAudioSettings()
    {
        using var sut = new VideoPreviewViewModel(null) { Volume = 42 };
        sut.ToggleMuteCommand.Execute(null);

        sut.LoadMedia(CreateMediaInfo());

        sut.Volume.Should().Be(42);
        sut.IsMuted.Should().BeTrue();
    }

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

    [Fact]
    public void CropSuggestion_IsShownUntilUserSetsCrop()
    {
        using var sut = new VideoPreviewViewModel(null);
        sut.LoadMedia(CreateMediaInfo());
        sut.HasMedia = true;

        sut.SuggestedCrop = new CropRect(0, 140, 1920, 800);

        sut.HasCropSuggestion.Should().BeTrue();
        sut.CropSuggestionText.Should().Be("Black bars: crop to 1920 x 800");

        sut.CropH = 1000;

        sut.HasCropSuggestion.Should().BeFalse();

        sut.ResetCropCommand.Execute(null);

        sut.HasCropSuggestion.Should().BeTrue();
    }

    [Fact]
    public void ApplySuggestedCrop_SetsCropAndHidesSuggestion()
    {
        using var sut = new VideoPreviewViewModel(null);
        sut.LoadMedia(CreateMediaInfo());
        sut.HasMedia = true;
        sut.SuggestedCrop = new CropRect(0, 140, 1920, 800);

        sut.ApplySuggestedCropCommand.Execute(null);

        sut.CropY.Should().Be(140);
        sut.CropW.Should().Be(1920);
        sut.CropH.Should().Be(800);
        sut.IsCropDirty.Should().BeTrue();
        sut.HasCropSuggestion.Should().BeFalse();
        sut.CropDetectionStatus.Should().Be("Applied suggested crop 1920 x 800 at 0, 140");
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

        public Task<CropRect?> SuggestAsync(
            SourceMediaInfo mediaInfo,
            CancellationToken ct = default) =>
            Task.FromResult<CropRect?>(null);
    }
}
