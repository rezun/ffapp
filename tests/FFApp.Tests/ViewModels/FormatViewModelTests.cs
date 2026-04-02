using FFApp.Core.Models;
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
            [VideoCodec.Vp8, VideoCodec.Vp9, VideoCodec.Av1],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void ChangeContainer_ToWebm_ResetsCodecToVp8()
    {
        _sut.SelectedContainer = OutputContainer.Webm;

        _sut.SelectedVideoCodec.Should().Be(VideoCodec.Vp8);
    }

    [Fact]
    public void ChangeContainer_ToMkv_AllFourCodecsAvailable()
    {
        _sut.SelectedContainer = OutputContainer.Mkv;

        _sut.AvailableVideoCodecs.Should().BeEquivalentTo(
            [VideoCodec.H264, VideoCodec.H265, VideoCodec.Vp8, VideoCodec.Vp9, VideoCodec.Av1],
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
    public void ChangeContainer_Mp4_AudioCodecListHasAacAndMp3()
    {
        _sut.SelectedContainer = OutputContainer.Webm;
        _sut.SelectedContainer = OutputContainer.Mp4;

        _sut.AvailableAudioCodecs.Should().BeEquivalentTo(
            [AudioCodec.Aac, AudioCodec.Mp3],
            o => o.WithStrictOrdering());
    }
}
