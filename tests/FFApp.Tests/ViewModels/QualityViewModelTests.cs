using FFApp.Core.Models;
using FFApp.ViewModels;
using FFApp.ViewModels.Quality;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public class QualityViewModelTests
{
    private readonly QualityViewModel _sut = new();

    [Fact]
    public void UpdateForCodec_H264_SetsExpectedDefaults()
    {
        _sut.UpdateForCodec(VideoCodec.H264);

        _sut.Active.Should().BeOfType<H26xQualitySettingsViewModel>();
        _sut.H264.Crf.Should().Be(23);
        _sut.H264.Preset.Should().Be("medium");
        _sut.H264.Profile.Should().Be("high");
    }

    [Fact]
    public void UpdateForCodec_H265_SetsExpectedDefaults()
    {
        _sut.UpdateForCodec(VideoCodec.H265);

        _sut.Active.Should().BeSameAs(_sut.H265);
        _sut.H265.Crf.Should().Be(28);
        _sut.H265.Preset.Should().Be("medium");
        _sut.H265.Profile.Should().Be("main");
    }

    [Fact]
    public void UpdateForCodec_Vp9_SetsExpectedDefaults()
    {
        _sut.UpdateForCodec(VideoCodec.Vp9);

        _sut.Active.Should().BeSameAs(_sut.Vpx);
        _sut.Vpx.Quality.Should().Be(32);
        _sut.Vpx.CpuUsed.Should().Be(2);
    }

    [Fact]
    public void UpdateForCodec_SwitchFromH264ToVp9_SwapsActive()
    {
        _sut.UpdateForCodec(VideoCodec.H264);
        _sut.Active.Should().BeSameAs(_sut.H264);

        _sut.UpdateForCodec(VideoCodec.Vp9);
        _sut.Active.Should().BeSameAs(_sut.Vpx);
    }

    [Fact]
    public void UpdateForCodec_SwitchFromVp9ToH265_SwapsActive()
    {
        _sut.UpdateForCodec(VideoCodec.Vp9);
        _sut.Active.Should().BeSameAs(_sut.Vpx);

        _sut.UpdateForCodec(VideoCodec.H265);
        _sut.Active.Should().BeSameAs(_sut.H265);
    }
}
