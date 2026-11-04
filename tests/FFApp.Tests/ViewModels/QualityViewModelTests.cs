using FFApp.Core.Models;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class QualityViewModelTests
{
    private readonly QualityViewModel _sut = new();

    [Theory]
    [InlineData(VideoCodec.H264)]
    [InlineData(VideoCodec.H265)]
    [InlineData(VideoCodec.Av1)]
    [InlineData(VideoCodec.Vp9)]
    public void UpdateForCodec_ActivatesThatCodecsSettingsWithModelDefaults(VideoCodec codec)
    {
        _sut.UpdateForCodec(codec);

        _sut.Active.Codec.Should().Be(codec);
        _sut.Active.BuildQualityOptions().Should().Be(DefaultQuality(codec));
    }

    private static QualityOptions DefaultQuality(VideoCodec codec) => codec switch
    {
        VideoCodec.H264 or VideoCodec.H265 => H26xQualityOptions.ForCodec(codec),
        VideoCodec.Av1 => new Av1QualityOptions(),
        VideoCodec.Vp9 => new Vp9QualityOptions(),
        _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, "Unsupported video codec."),
    };
}
