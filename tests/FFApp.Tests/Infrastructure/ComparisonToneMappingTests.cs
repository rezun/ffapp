using FFApp.Infrastructure;
using FFmpeg.AutoGen;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class ComparisonToneMappingTests
{
    [Theory]
    [InlineData(AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084, true)]
    [InlineData(AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67, true)]
    [InlineData(AVColorTransferCharacteristic.AVCOL_TRC_BT709, false)]
    [InlineData(AVColorTransferCharacteristic.AVCOL_TRC_IEC61966_2_1, false)]
    [InlineData(AVColorTransferCharacteristic.AVCOL_TRC_UNSPECIFIED, false)]
    public void IsHdr_DetectsPqAndHlgOnly(AVColorTransferCharacteristic transfer, bool expected) =>
        ComparisonToneMapping.IsHdr(transfer).Should().Be(expected);

    [Fact]
    public void FilterGraph_BuildsClutFromSourceTransfer_AndMapsVideoToRgb48()
    {
        var graph = ComparisonToneMapping.FilterGraph("smpte2084", "bt2020", null);
        graph.Should().Contain("haldclutsrc=level=12,trim=end_frame=1");
        graph.Should().Contain("setparams=colorspace=gbr:range=pc:color_trc=smpte2084:color_primaries=bt2020");
        graph.Should().Contain("zscale=t=linear:npl=100:p=bt709,format=gbrpf32le,tonemap=hable,zscale=t=bt709");
        graph.Should().Contain("[clut];[in]");
        graph.Should().EndWith("[video][clut]haldclut=interp=tetrahedral:repeatlast=1[out]");
    }

    [Theory]
    [InlineData(1000.0, "tonemap=hable:peak=10,")]
    [InlineData(4000.0, "tonemap=hable:peak=40,")]
    [InlineData(null, "tonemap=hable,")]
    [InlineData(0.0, "tonemap=hable,")]
    public void FilterGraph_PassesContentPeakRelativeToReferenceWhite(double? peakNits, string expected) =>
        ComparisonToneMapping.FilterGraph("arib-std-b67", "bt2020", peakNits).Should().Contain(expected);

    [Fact]
    public void VideoToolboxFilterGraph_UploadsOnlySoftwareFrames_AndReturnsBt709()
    {
        var conversion = "scale_vt=color_matrix=bt709:color_primaries=bt709:color_transfer=bt709,hwdownload,format=p010le[out]";
        ComparisonToneMapping.VideoToolboxFilterGraph(hardwareFrames: true).Should().Be("[in]" + conversion);
        ComparisonToneMapping.VideoToolboxFilterGraph(hardwareFrames: false)
            .Should().Be("[in]format=p010le,hwupload," + conversion);
    }
}
