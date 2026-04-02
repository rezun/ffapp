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
}
