using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public class SourcePanelViewModelTests
{
    private readonly IMediaProbeService _probeService = Substitute.For<IMediaProbeService>();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();
    private readonly IMessenger _messenger = Substitute.For<IMessenger>();
    private readonly SourcePanelViewModel _sut;

    public SourcePanelViewModelTests()
    {
        _sut = new SourcePanelViewModel(_probeService, _dialogService, _messenger);
    }

    [Fact]
    public void MediaSummaryText_WhenNoMediaInfo_ReturnsEmpty()
    {
        _sut.MediaSummaryText.Should().BeEmpty();
    }

    [Fact]
    public async Task ChooseFileAsync_DialogReturnsNull_NoProbe()
    {
        _dialogService.OpenFileAsync(Arg.Any<string>(), Arg.Any<string?>())
            .Returns((string?)null);

        await _sut.ChooseFileCommand.ExecuteAsync(null);

        await _probeService.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PastePathAsync_ClipboardEmpty_NoProbe()
    {
        _dialogService.GetClipboardTextAsync()
            .Returns((string?)null);

        await _sut.PastePathCommand.ExecuteAsync(null);

        await _probeService.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoadSource_NonexistentFile_SetsFileNotFoundStatus()
    {
        await _sut.LoadSource("/does/not/exist.mp4");

        _sut.StatusText.Should().Be("File not found.");
    }

    [Fact]
    public void Default_StatusText_IsNoSourceFileLoaded()
    {
        _sut.StatusText.Should().Be("No source file loaded.");
    }

    [Fact]
    public void Default_MediaInfo_IsNull()
    {
        _sut.MediaInfo.Should().BeNull();
    }
}
