using System.Collections.Immutable;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public class SourceListPanelViewModelTests
{
    private readonly IMediaProbeService _probeService = Substitute.For<IMediaProbeService>();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();
    private readonly IMessenger _messenger = Substitute.For<IMessenger>();
    private readonly SourceListPanelViewModel _sut;

    public SourceListPanelViewModelTests()
    {
        _sut = new SourceListPanelViewModel(_probeService, _dialogService, _messenger);
    }

    [Fact]
    public async Task AddFilesAsync_DialogReturnsNull_NoProbe()
    {
        _dialogService.OpenFilesAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns((string[]?)null);

        await _sut.AddFilesCommand.ExecuteAsync(null);

        await _probeService.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddPathsAsync_StoresCropSuggestionForNewSource()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ffapp-crop-{Guid.NewGuid():N}.mkv");
        await File.WriteAllBytesAsync(path, []);
        try
        {
            var mediaInfo = new SourceMediaInfo
            {
                SourcePath = path,
                DurationSeconds = 7200,
                VideoStreams = ImmutableArray.Create(new VideoStreamInfo(0, Width: 1920, Height: 1080)),
            };
            _probeService.ProbeAsync(path, Arg.Any<CancellationToken>()).Returns(mediaInfo);
            var cropDetection = Substitute.For<ICropDetectionService>();
            cropDetection.SuggestAsync(mediaInfo, Arg.Any<CancellationToken>())
                .Returns(new CropRect(0, 140, 1920, 800));
            var sut = new SourceListPanelViewModel(_probeService, _dialogService, _messenger, cropDetection);

            await sut.AddPathsAsync([path]);

            var source = sut.Sources.Should().ContainSingle().Subject;
            source.SuggestedCrop.Should().Be(new CropRect(0, 140, 1920, 800));
            source.IsAnalyzingCrop.Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }
}
