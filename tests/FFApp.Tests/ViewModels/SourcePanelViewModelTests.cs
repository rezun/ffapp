using CommunityToolkit.Mvvm.Messaging;
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
    public void MediaSummaryText_WhenNoSelectedSource_ReturnsEmpty()
    {
        _sut.MediaSummaryText.Should().BeEmpty();
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
    public void Default_StatusText_IsNoSourceFilesLoaded()
    {
        _sut.StatusText.Should().Be("No source files loaded.");
    }

    [Fact]
    public void Default_SelectedSource_IsNull()
    {
        _sut.SelectedSource.Should().BeNull();
    }

    [Fact]
    public void Default_Sources_IsEmpty()
    {
        _sut.Sources.Should().BeEmpty();
    }
}
