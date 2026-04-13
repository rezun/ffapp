using FFApp.Core.Models;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class QualityViewModelTests
{
    private readonly QualityViewModel _sut = new();

    [Fact]
    public void UpdateForCodec_H264_SetsExpectedDefaults()
    {
        _sut.UpdateForCodec(VideoCodec.H264);

        _sut.IsH26xCodec.Should().BeTrue();
        _sut.Crf.Should().Be(23);
        _sut.H26xPreset.Should().Be("medium");
        _sut.H26xProfile.Should().Be("high");
    }

    [Fact]
    public void UpdateForCodec_H265_SetsExpectedDefaults()
    {
        _sut.UpdateForCodec(VideoCodec.H265);

        _sut.IsH26xCodec.Should().BeTrue();
        _sut.Crf.Should().Be(28);
        _sut.H26xPreset.Should().Be("medium");
        _sut.H26xProfile.Should().Be("main");
    }

    [Fact]
    public void UpdateForCodec_Vp9_SetsExpectedDefaults()
    {
        _sut.UpdateForCodec(VideoCodec.Vp9);

        _sut.IsH26xCodec.Should().BeFalse();
        _sut.VpxQuality.Should().Be(32);
        _sut.VpxCpuUsed.Should().Be(2);
    }

    [Fact]
    public void UpdateForCodec_SwitchFromH264ToVp9_ClearsH26xFlag()
    {
        _sut.UpdateForCodec(VideoCodec.H264);
        _sut.IsH26xCodec.Should().BeTrue();

        _sut.UpdateForCodec(VideoCodec.Vp9);
        _sut.IsH26xCodec.Should().BeFalse();
    }

    [Fact]
    public void UpdateForCodec_SwitchFromVp9ToH265_SetsH26xFlag()
    {
        _sut.UpdateForCodec(VideoCodec.Vp9);
        _sut.IsH26xCodec.Should().BeFalse();

        _sut.UpdateForCodec(VideoCodec.H265);
        _sut.IsH26xCodec.Should().BeTrue();
    }
}
