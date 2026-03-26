using FFApp.Core.Models;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class EncodingStrategyViewModelTests
{
    private readonly EncodingStrategyViewModel _sut = new();

    [Fact]
    public void Default_StrategyIsTargetQuality_ActiveIsQuality()
    {
        _sut.SelectedStrategy.Should().Be(EncodingStrategy.TargetQuality);
        _sut.ActiveStrategyViewModel.Should().BeSameAs(_sut.Quality);
    }

    [Fact]
    public void SwitchToTargetBitrate_ActiveIsBitrate()
    {
        _sut.SelectedStrategy = EncodingStrategy.TargetBitrate;

        _sut.ActiveStrategyViewModel.Should().BeSameAs(_sut.Bitrate);
    }

    [Fact]
    public void SwitchToTargetFileSize_ActiveIsFileSize()
    {
        _sut.SelectedStrategy = EncodingStrategy.TargetFileSize;

        _sut.ActiveStrategyViewModel.Should().BeSameAs(_sut.FileSize);
    }

    [Fact]
    public void SwitchBackToTargetQuality_ActiveIsQuality()
    {
        _sut.SelectedStrategy = EncodingStrategy.TargetBitrate;
        _sut.ActiveStrategyViewModel.Should().BeSameAs(_sut.Bitrate);

        _sut.SelectedStrategy = EncodingStrategy.TargetQuality;
        _sut.ActiveStrategyViewModel.Should().BeSameAs(_sut.Quality);
    }
}
