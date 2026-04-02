using FFApp.Core.Models;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class MediaProbeServiceParsingTests
{
    // --- ParseMediaInfo ---

    [Fact]
    public void ParseMediaInfo_FullOutput_ParsesAllStreams()
    {
        var json = """
        {
            "format": {
                "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
                "duration": "120.5",
                "bit_rate": "5000000"
            },
            "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "bit_rate": "4500000", "width": 1920, "height": 1080, "avg_frame_rate": "24000/1001", "nb_frames": "2880" },
                { "index": 1, "codec_type": "audio", "codec_name": "aac", "bit_rate": "128000", "channels": 2, "sample_rate": "48000", "tags": { "language": "eng" } },
                { "index": 2, "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "eng" } },
                { "index": 3, "codec_type": "data", "codec_name": "bin_data" }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);

        result.SourcePath.Should().Be("/test/video.mp4");
        result.ContainerFormat.Should().Be("mov,mp4,m4a,3gp,3g2,mj2");
        result.DurationSeconds.Should().BeApproximately(120.5, 0.001);
        result.BitRate.Should().Be(5_000_000);
        result.VideoStreams.Should().HaveCount(1);
        result.AudioStreams.Should().HaveCount(1);
        result.SubtitleStreams.Should().HaveCount(1);
        result.DataStreams.Should().HaveCount(1);
    }

    [Fact]
    public void ParseMediaInfo_VideoStream_ParsesAllFields()
    {
        var json = """
        {
            "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "bit_rate": "4500000", "width": 1920, "height": 1080, "avg_frame_rate": "24000/1001", "nb_frames": "2880" }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        var video = result.PrimaryVideoStream!;

        video.Index.Should().Be(0);
        video.CodecName.Should().Be("h264");
        video.BitRate.Should().Be(4_500_000);
        video.Width.Should().Be(1920);
        video.Height.Should().Be(1080);
        video.FrameRate.Should().BeApproximately(23.976, 0.001);
        video.FrameCount.Should().Be(2880);
    }

    [Fact]
    public void ParseMediaInfo_AudioStream_ParsesLanguageFromTags()
    {
        var json = """
        {
            "streams": [
                { "index": 1, "codec_type": "audio", "codec_name": "aac", "bit_rate": "128000", "channels": 2, "sample_rate": "44100", "tags": { "language": "jpn" } }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        result.AudioStreams[0].Language.Should().Be("jpn");
    }

    [Fact]
    public void ParseMediaInfo_AttachmentStream_TreatedAsData()
    {
        var json = """
        {
            "streams": [
                { "index": 3, "codec_type": "attachment", "codec_name": "ttf" }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mkv", json);
        result.DataStreams.Should().HaveCount(1);
        result.DataStreams[0].CodecName.Should().Be("ttf");
    }

    [Fact]
    public void ParseMediaInfo_MissingFormat_ReturnsNulls()
    {
        var json = """{ "streams": [] }""";
        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);

        result.ContainerFormat.Should().BeNull();
        result.DurationSeconds.Should().BeNull();
        result.BitRate.Should().BeNull();
    }

    [Fact]
    public void ParseMediaInfo_MissingStreams_ReturnsEmptyArrays()
    {
        var json = """{ "format": { "format_name": "mp4" } }""";
        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);

        result.VideoStreams.Should().BeEmpty();
        result.AudioStreams.Should().BeEmpty();
        result.SubtitleStreams.Should().BeEmpty();
        result.DataStreams.Should().BeEmpty();
    }

    [Fact]
    public void ParseMediaInfo_NumericFieldsAsStrings_ParsedCorrectly()
    {
        var json = """
        {
            "format": { "duration": "60.5", "bit_rate": "5000000" },
            "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "bit_rate": "4500000", "width": 1920, "height": 1080, "avg_frame_rate": "30/1", "nb_frames": "1800" }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        result.DurationSeconds.Should().BeApproximately(60.5, 0.001);
        result.BitRate.Should().Be(5_000_000);
        result.PrimaryVideoStream!.BitRate.Should().Be(4_500_000);
    }

    [Fact]
    public void ParseMediaInfo_InvalidJson_ThrowsMediaProbeException()
    {
        var act = () => MediaProbeService.ParseMediaInfo("/test/video.mp4", "not json");
        act.Should().Throw<MediaProbeException>().WithMessage("*parse*");
    }

    [Fact]
    public void ParseMediaInfo_MultipleVideoStreams_AllCaptured()
    {
        var json = """
        {
            "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "avg_frame_rate": "24/1" },
                { "index": 1, "codec_type": "video", "codec_name": "mjpeg", "width": 320, "height": 240, "avg_frame_rate": "0/0" }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        result.VideoStreams.Should().HaveCount(2);
        result.PrimaryVideoStream!.CodecName.Should().Be("h264");
    }

    // --- ParseFrameRate ---

    [Fact]
    public void ParseFrameRate_FractionFormat_Correct()
    {
        MediaProbeService.ParseFrameRate("24000/1001").Should().BeApproximately(23.976, 0.001);
    }

    [Fact]
    public void ParseFrameRate_IntegerFraction_Correct()
    {
        MediaProbeService.ParseFrameRate("30/1").Should().BeApproximately(30.0, 0.001);
    }

    [Fact]
    public void ParseFrameRate_DecimalFormat_Correct()
    {
        MediaProbeService.ParseFrameRate("29.97").Should().BeApproximately(29.97, 0.001);
    }

    [Fact]
    public void ParseFrameRate_ZeroDenominator_ReturnsNull()
    {
        MediaProbeService.ParseFrameRate("30/0").Should().BeNull();
    }

    [Fact]
    public void ParseFrameRate_BothZero_ReturnsNull()
    {
        MediaProbeService.ParseFrameRate("0/0").Should().BeNull();
    }

    [Fact]
    public void ParseFrameRate_NAString_ReturnsNull()
    {
        MediaProbeService.ParseFrameRate("N/A").Should().BeNull();
    }

    [Fact]
    public void ParseFrameRate_NullInput_ReturnsNull()
    {
        MediaProbeService.ParseFrameRate(null).Should().BeNull();
    }

    [Fact]
    public void ParseFrameRate_EmptyString_ReturnsNull()
    {
        MediaProbeService.ParseFrameRate("").Should().BeNull();
    }

    // --- Rotation parsing ---

    [Fact]
    public void ParseMediaInfo_RotationInTags_ParsedCorrectly()
    {
        var json = """
        {
            "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 3840, "height": 2160, "avg_frame_rate": "30/1", "tags": { "rotate": "90" } }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        result.PrimaryVideoStream!.Rotation.Should().Be(90);
        result.PrimaryVideoStream.DisplayWidth.Should().Be(2160);
        result.PrimaryVideoStream.DisplayHeight.Should().Be(3840);
    }

    [Fact]
    public void ParseMediaInfo_RotationInSideData_ParsedCorrectly()
    {
        var json = """
        {
            "streams": [
                {
                    "index": 0, "codec_type": "video", "codec_name": "h264", "width": 3840, "height": 2160, "avg_frame_rate": "30/1",
                    "side_data_list": [
                        { "side_data_type": "Display Matrix", "rotation": -90 }
                    ]
                }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        result.PrimaryVideoStream!.Rotation.Should().Be(-90);
        result.PrimaryVideoStream.DisplayWidth.Should().Be(2160);
        result.PrimaryVideoStream.DisplayHeight.Should().Be(3840);
    }

    [Fact]
    public void ParseMediaInfo_NoRotation_DisplayDimensionsMatchCoded()
    {
        var json = """
        {
            "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "avg_frame_rate": "30/1" }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        result.PrimaryVideoStream!.Rotation.Should().BeNull();
        result.PrimaryVideoStream.DisplayWidth.Should().Be(1920);
        result.PrimaryVideoStream.DisplayHeight.Should().Be(1080);
    }

    [Fact]
    public void ParseMediaInfo_Rotation180_DisplayDimensionsUnchanged()
    {
        var json = """
        {
            "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "avg_frame_rate": "30/1", "tags": { "rotate": "180" } }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        result.PrimaryVideoStream!.Rotation.Should().Be(180);
        result.PrimaryVideoStream.DisplayWidth.Should().Be(1920);
        result.PrimaryVideoStream.DisplayHeight.Should().Be(1080);
    }

    // --- HDR metadata parsing ---

    [Fact]
    public void ParseMediaInfo_Hdr10Stream_ExtractsColorProperties()
    {
        var json = """
        {
            "streams": [{
                "index": 0, "codec_type": "video", "codec_name": "hevc",
                "width": 3840, "height": 2160, "avg_frame_rate": "24000/1001",
                "pix_fmt": "yuv420p10le",
                "color_primaries": "bt2020",
                "color_transfer": "smpte2084",
                "color_space": "bt2020nc",
                "color_range": "tv",
                "bits_per_raw_sample": "10"
            }]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/hdr.mkv", json);
        var video = result.PrimaryVideoStream!;
        video.PixelFormat.Should().Be("yuv420p10le");
        video.ColorPrimaries.Should().Be("bt2020");
        video.ColorTransfer.Should().Be("smpte2084");
        video.ColorSpace.Should().Be("bt2020nc");
        video.ColorRange.Should().Be("tv");
        video.BitsPerRawSample.Should().Be(10);
        video.HdrFormat.Should().Be(HdrFormat.Hdr10);
        video.IsHdr.Should().BeTrue();
    }

    [Fact]
    public void ParseMediaInfo_HlgStream_ClassifiesAsHlg()
    {
        var json = """
        {
            "streams": [{
                "index": 0, "codec_type": "video", "codec_name": "hevc",
                "width": 3840, "height": 2160, "avg_frame_rate": "50/1",
                "pix_fmt": "yuv420p10le",
                "color_primaries": "bt2020",
                "color_transfer": "arib-std-b67",
                "color_space": "bt2020nc",
                "bits_per_raw_sample": "10"
            }]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/hlg.mkv", json);
        var video = result.PrimaryVideoStream!;
        video.HdrFormat.Should().Be(HdrFormat.Hlg);
        video.IsHdr.Should().BeTrue();
    }

    [Fact]
    public void ParseMediaInfo_SdrStream_ClassifiesAsSdr()
    {
        var json = """
        {
            "streams": [{
                "index": 0, "codec_type": "video", "codec_name": "h264",
                "width": 1920, "height": 1080, "avg_frame_rate": "30/1",
                "pix_fmt": "yuv420p",
                "color_primaries": "bt709",
                "color_transfer": "bt709",
                "color_space": "bt709"
            }]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/sdr.mp4", json);
        var video = result.PrimaryVideoStream!;
        video.HdrFormat.Should().Be(HdrFormat.Sdr);
        video.IsHdr.Should().BeFalse();
        video.HdrMetadata.Should().BeNull();
    }

    [Fact]
    public void ParseMediaInfo_NoColorProperties_ClassifiesAsSdr()
    {
        var json = """
        {
            "streams": [{
                "index": 0, "codec_type": "video", "codec_name": "h264",
                "width": 1920, "height": 1080, "avg_frame_rate": "30/1"
            }]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        var video = result.PrimaryVideoStream!;
        video.ColorPrimaries.Should().BeNull();
        video.ColorTransfer.Should().BeNull();
        video.HdrFormat.Should().Be(HdrFormat.Sdr);
        video.IsHdr.Should().BeFalse();
    }

    [Fact]
    public void ParseMediaInfo_Hdr10WithContentLightLevel_ExtractsMaxCllMaxFall()
    {
        var json = """
        {
            "streams": [{
                "index": 0, "codec_type": "video", "codec_name": "hevc",
                "width": 3840, "height": 2160, "avg_frame_rate": "24/1",
                "pix_fmt": "yuv420p10le",
                "color_primaries": "bt2020",
                "color_transfer": "smpte2084",
                "color_space": "bt2020nc",
                "side_data_list": [
                    {
                        "side_data_type": "Content light level metadata",
                        "max_content": 1000,
                        "max_average": 400
                    }
                ]
            }]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/hdr.mkv", json);
        var metadata = result.PrimaryVideoStream!.HdrMetadata;
        metadata.Should().NotBeNull();
        metadata!.MaxContentLightLevel.Should().Be(1000);
        metadata.MaxAverageLightLevel.Should().Be(400);
        metadata.MasteringDisplay.Should().BeNull();
    }

    [Fact]
    public void ParseMediaInfo_Hdr10WithMasteringDisplay_ExtractsAllFields()
    {
        var json = """
        {
            "streams": [{
                "index": 0, "codec_type": "video", "codec_name": "hevc",
                "width": 3840, "height": 2160, "avg_frame_rate": "24/1",
                "pix_fmt": "yuv420p10le",
                "color_primaries": "bt2020",
                "color_transfer": "smpte2084",
                "color_space": "bt2020nc",
                "side_data_list": [
                    {
                        "side_data_type": "Mastering display metadata",
                        "red_x": "13250",
                        "red_y": "34500",
                        "green_x": "7500",
                        "green_y": "3000",
                        "blue_x": "34000",
                        "blue_y": "16000",
                        "white_point_x": "15635",
                        "white_point_y": "16450",
                        "min_luminance": "50",
                        "max_luminance": "10000000"
                    },
                    {
                        "side_data_type": "Content light level metadata",
                        "max_content": 1000,
                        "max_average": 400
                    }
                ]
            }]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/hdr.mkv", json);
        var metadata = result.PrimaryVideoStream!.HdrMetadata!;
        metadata.MaxContentLightLevel.Should().Be(1000);
        metadata.MaxAverageLightLevel.Should().Be(400);

        var md = metadata.MasteringDisplay!;
        md.RedX.Should().Be("13250");
        md.RedY.Should().Be("34500");
        md.GreenX.Should().Be("7500");
        md.GreenY.Should().Be("3000");
        md.BlueX.Should().Be("34000");
        md.BlueY.Should().Be("16000");
        md.WhitePointX.Should().Be("15635");
        md.WhitePointY.Should().Be("16450");
        md.MinLuminance.Should().Be("50");
        md.MaxLuminance.Should().Be("10000000");
    }

    [Fact]
    public void ParseMediaInfo_SideDataWithoutHdrEntries_ReturnsNullMetadata()
    {
        var json = """
        {
            "streams": [{
                "index": 0, "codec_type": "video", "codec_name": "h264",
                "width": 1920, "height": 1080, "avg_frame_rate": "30/1",
                "side_data_list": [
                    { "side_data_type": "Display Matrix", "rotation": -90 }
                ]
            }]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);
        result.PrimaryVideoStream!.HdrMetadata.Should().BeNull();
        result.PrimaryVideoStream.Rotation.Should().Be(-90);
    }
}
