using FFApp.Core.Models;
using FFApp.Core.Validation;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class FormatViewModelTests
{
    private readonly FormatViewModel _sut = new();

    [Theory]
    [InlineData(OutputContainer.Webm, VideoCodec.Av1)]
    [InlineData(OutputContainer.Mkv, VideoCodec.H264)]
    public void ChangeContainer_OffersItsCodecs_AndKeepsTheVideoCodecOnlyIfAllowed(
        OutputContainer container, VideoCodec expectedCodec)
    {
        _sut.SelectedContainer = container;

        _sut.AvailableVideoCodecs.Should().Equal(CompatibilityData.AllowedVideoCodecs(container));
        _sut.AvailableAudioCodecs.Should().Equal(CompatibilityData.AllowedAudioCodecs(container));
        _sut.SelectedVideoCodec.Should().Be(expectedCodec);
    }

    [Fact]
    public void ChangeMode_AfterAudioOnlyContainer_RefreshesVideoCodecsForNewContainer()
    {
        _sut.SelectedVideoMode = VideoMode.Remove;
        _sut.SelectedContainer = OutputContainer.Mka;

        _sut.SelectedVideoMode = VideoMode.Copy;

        _sut.SelectedContainer.Should().Be(OutputContainer.Mp4);
        _sut.AvailableVideoCodecs.Should().Equal(CompatibilityData.AllowedVideoCodecs(OutputContainer.Mp4));
        _sut.AvailableVideoCodecs.Should().Contain(_sut.SelectedVideoCodec);
    }
}
