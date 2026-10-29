using FFApp.Core.Models;
using FFApp.Core.Validation;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class FormatViewModelTests
{
    private readonly FormatViewModel _sut = new();

    [Fact]
    public void Defaults_ContainerIsMp4_CodecIsH264()
    {
        _sut.SelectedContainer.Should().Be(OutputContainer.Mp4);
        _sut.SelectedVideoCodec.Should().Be(VideoCodec.H264);
    }

    [Fact]
    public void ChangeContainer_ToWebm_VideoCodecListUpdatesToVpxOnly()
    {
        _sut.SelectedContainer = OutputContainer.Webm;

        _sut.AvailableVideoCodecs.Should().BeEquivalentTo(
            [VideoCodec.Av1, VideoCodec.Vp9],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void ChangeContainer_ToWebm_ResetsCodecToAv1()
    {
        _sut.SelectedContainer = OutputContainer.Webm;

        _sut.SelectedVideoCodec.Should().Be(VideoCodec.Av1);
    }

    [Fact]
    public void ChangeContainer_ToMkv_AllFourCodecsAvailable()
    {
        _sut.SelectedContainer = OutputContainer.Mkv;

        _sut.AvailableVideoCodecs.Should().BeEquivalentTo(
            [VideoCodec.Av1, VideoCodec.H265, VideoCodec.H264, VideoCodec.Vp9],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void ChangeContainer_ToMkv_KeepsH264WhenStillValid()
    {
        _sut.SelectedVideoCodec.Should().Be(VideoCodec.H264);

        _sut.SelectedContainer = OutputContainer.Mkv;

        _sut.SelectedVideoCodec.Should().Be(VideoCodec.H264);
    }

    [Fact]
    public void ChangeContainer_AudioCodecListUpdates()
    {
        _sut.SelectedContainer = OutputContainer.Webm;

        _sut.AvailableAudioCodecs.Should().BeEquivalentTo(
            [AudioCodec.Opus, AudioCodec.Vorbis],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void ChangeContainer_Mp4_AudioCodecListHasAacMp3AndOpus()
    {
        _sut.SelectedContainer = OutputContainer.Webm;
        _sut.SelectedContainer = OutputContainer.Mp4;

        _sut.AvailableAudioCodecs.Should().BeEquivalentTo(
            [AudioCodec.Aac, AudioCodec.Mp3, AudioCodec.Opus],
            o => o.WithStrictOrdering());
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
