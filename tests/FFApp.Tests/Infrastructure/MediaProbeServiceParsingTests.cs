using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public class MediaProbeServiceParsingTests
{
    // --- ParseMediaInfo ---

    [Theory]
    [InlineData("5.1(side)")]
    [InlineData(null)]
    public void ParseMediaInfo_AudioStream_ParsesOptionalChannelLayout(string? layout)
    {
        var layoutField = layout is null ? "" : $", \"channel_layout\": \"{layout}\"";
        var json = "{\"streams\":[{\"index\":1,\"codec_type\":\"audio\",\"channels\":6" + layoutField + "}]}";

        var result = MediaProbeService.ParseMediaInfo("/test/video.mkv", json);

        result.AudioStreams[0].ChannelLayout.Should().Be(layout);
    }

    [Fact]
    public void ParseMediaInfo_FullOutput_ParsesFormatAndEveryStreamType()
    {
        var json = """
        {
            "format": {
                "format_name": "mov,mp4,m4a,3gp,3g2,mj2",
                "duration": "120.5",
                "bit_rate": "5000000"
            },
            "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "hevc", "bit_rate": "4500000", "width": 3840, "height": 2160,
                  "avg_frame_rate": "24000/1001", "nb_frames": "2880", "pix_fmt": "yuv420p10le", "color_primaries": "bt2020",
                  "color_transfer": "smpte2084", "color_space": "bt2020nc", "color_range": "tv", "bits_per_raw_sample": "10" },
                { "index": 1, "codec_type": "audio", "codec_name": "aac", "bit_rate": "128000", "channels": 2, "sample_rate": "48000", "tags": { "language": "jpn" } },
                { "index": 2, "codec_type": "subtitle", "codec_name": "subrip", "tags": { "language": "eng" } },
                { "index": 3, "codec_type": "data", "codec_name": "bin_data" }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mp4", json);

        result.Should().BeEquivalentTo(new SourceMediaInfo
        {
            SourcePath = "/test/video.mp4",
            ContainerFormat = "mov,mp4,m4a,3gp,3g2,mj2",
            DurationSeconds = 120.5,
            BitRate = 5_000_000,
            VideoStreams = [new VideoStreamInfo(0, "hevc", 4_500_000, 3840, 2160, 24000 / 1001.0, 2880, null,
                "yuv420p10le", "bt2020", "smpte2084", "bt2020nc", "tv", 10)],
            AudioStreams = [new AudioStreamInfo(1, "aac", 128_000, 2, 48_000, "jpn") { MetadataKeys = ["language"] }],
            SubtitleStreams = [new SubtitleStreamInfo(2, "subrip", "eng")],
            DataStreams = [new DataStreamInfo(3, "bin_data")],
        }, options => options.Excluding(member => member.Name == nameof(VideoStreamInfo.Details)));
    }

    [Fact]
    public void ParseMediaInfo_MetadataKeys_CapturesFormatVideoAndAudioTags()
    {
        var json = """
        {
            "format": {
                "tags": { "title": "Movie", "encoding_tool": "old encoder" }
            },
            "streams": [
                {
                    "index": 0,
                    "codec_type": "video",
                    "tags": { "language": "eng", "BPS-eng": "50000000" }
                },
                {
                    "index": 1,
                    "codec_type": "audio",
                    "tags": { "language": "deu", "NUMBER_OF_BYTES-deu": "123" }
                }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/video.mkv", json);

        result.MetadataKeys.Should().BeEquivalentTo("title", "encoding_tool");
        result.VideoStreams[0].MetadataKeys.Should().BeEquivalentTo("language", "BPS-eng");
        result.AudioStreams[0].MetadataKeys.Should().BeEquivalentTo("language", "NUMBER_OF_BYTES-deu");
    }

    [Fact]
    public void ParseMediaInfo_AttachedPictures_AreSeparatedFromVideoStreams()
    {
        var media = MediaProbeService.ParseMediaInfo("/test/movie.mkv", """
            { "streams": [
                { "index": 7, "codec_type": "video", "codec_name": "png", "disposition": { "attached_pic": 1 },
                  "tags": { "filename": "Original Release.png", "mimetype": "image/png" } },
                { "index": 0, "codec_type": "video", "codec_name": "hevc", "disposition": { "attached_pic": 0 } },
                { "index": 8, "codec_type": "video", "codec_name": "mjpeg", "disposition": { "attached_pic": 1 } },
                { "index": 1, "codec_type": "video", "codec_name": "mjpeg" }
            ] }
            """);

        media.VideoStreams.Select(stream => stream.Index).Should().Equal(0, 1);
        (media.PrimaryVideoStream?.CodecName).Should().Be("hevc");
        media.AttachedPictureStreams.Should().Equal(
            new AttachedPictureStreamInfo(7, "png", "Original Release.png", "image/png"),
            new AttachedPictureStreamInfo(8, "mjpeg"));
        media.StreamCount.Should().Be(4);
        FormatHelpers.BuildMediaSummaryText(media).Should().Contain("Attached pictures: 2");
    }

    [Fact]
    public void ParseMediaInfo_MissingFormatAndStreams_ReturnsEmptyInfo()
    {
        MediaProbeService.ParseMediaInfo("/test/video.mp4", "{}")
            .Should().BeEquivalentTo(new SourceMediaInfo { SourcePath = "/test/video.mp4" });
    }

    [Fact]
    public void ParseMediaInfo_InvalidJson_ThrowsMediaProbeException()
    {
        var act = () => MediaProbeService.ParseMediaInfo("/test/video.mp4", "not json");
        act.Should().Throw<MediaProbeException>().WithMessage("*parse*");
    }

    // --- ParseFrameRate ---

    [Theory]
    [InlineData("24000/1001", 24000 / 1001.0)]
    [InlineData("29.97", 29.97)]
    [InlineData("30/0", null)]
    [InlineData("0/0", null)]
    [InlineData("N/A", null)]
    [InlineData(null, null)]
    public void ParseFrameRate_ParsesFractionsAndDecimals_RejectsUnknownOrZeroDenominator(string? value, double? expected)
    {
        MediaProbeService.ParseFrameRate(value).Should().Be(expected);
    }

    // --- Rotation parsing ---

    [Theory]
    [InlineData("", null, false)]
    [InlineData(""", "tags": { "rotate": "90" }""", 90, true)]
    [InlineData(""", "tags": { "rotate": "180" }""", 180, false)]
    [InlineData(""", "side_data_list": [{ "side_data_type": "Display Matrix", "rotation": -90 }]""", -90, true)]
    public void ParseMediaInfo_Rotation_FromTagsOrDisplayMatrix_SwapsDisplaySizeForQuarterTurns(
        string rotationJson, int? rotation, bool swapped)
    {
        var json = """{ "streams": [{ "index": 0, "codec_type": "video", "width": 1920, "height": 1080""" + rotationJson + "}] }";

        var video = MediaProbeService.ParseMediaInfo("/test/video.mp4", json).VideoStreams[0];

        video.Rotation.Should().Be(rotation);
        video.DisplayWidth.Should().Be(swapped ? 1080 : 1920);
        video.DisplayHeight.Should().Be(swapped ? 1920 : 1080);
        video.HdrMetadata.Should().BeNull("a display matrix is not HDR side data");
    }

    // --- HDR classification and metadata ---

    [Theory]
    [InlineData("smpte2084", "bt2020", HdrFormat.Hdr10)]
    [InlineData("arib-std-b67", "bt2020", HdrFormat.Hlg)]
    [InlineData("bt709", "bt709", HdrFormat.Sdr)]
    [InlineData("smpte2084", "bt709", HdrFormat.Sdr)]
    public void HdrFormat_RequiresPqOrHlgTransferWithBt2020Primaries(string transfer, string primaries, HdrFormat expected)
    {
        var video = new VideoStreamInfo(0, ColorPrimaries: primaries, ColorTransfer: transfer);

        video.HdrFormat.Should().Be(expected);
        video.IsHdr.Should().Be(expected != HdrFormat.Sdr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParseMediaInfo_HdrSideData_ExtractsLightLevelsAndOptionalMasteringDisplay(bool withMastering)
    {
        const string mastering = """
            { "side_data_type": "Mastering display metadata", "red_x": "13250", "red_y": "34500", "green_x": "7500",
              "green_y": "3000", "blue_x": "34000", "blue_y": "16000", "white_point_x": "15635", "white_point_y": "16450",
              "min_luminance": "50", "max_luminance": "10000000" },
            """;
        const string lightLevel = """{ "side_data_type": "Content light level metadata", "max_content": 1000, "max_average": 400 }""";
        var json = """{ "streams": [{ "index": 0, "codec_type": "video", "side_data_list": [""" + (withMastering ? mastering : "") + lightLevel + "] }] }";

        var metadata = MediaProbeService.ParseMediaInfo("/test/hdr.mkv", json).VideoStreams[0].HdrMetadata;

        metadata.Should().Be(new HdrMetadata(1000, 400, withMastering
            ? new MasteringDisplayMetadata("13250", "34500", "7500", "3000", "34000", "16000", "15635", "16450", "50", "10000000")
            : null));
    }

    // --- Track details, dispositions and chapters ---

    [Fact]
    public void ParseMediaInfo_ReadsTrackTitleLanguageAndDispositions()
    {
        var json = """
        {
            "streams": [
                { "index": 1, "codec_type": "audio", "codec_name": "dts", "profile": "DTS-HD MA", "bits_per_raw_sample": "24",
                  "duration": "12.5",
                  "disposition": { "default": 1, "forced": 0, "hearing_impaired": 0, "visual_impaired": 1, "comment": 1, "original": 1 },
                  "tags": { "title": "Commentary", "language": "eng", "BPS-eng": "1509000" } },
                { "index": 2, "codec_type": "subtitle", "codec_name": "subrip",
                  "disposition": { "default": 0, "forced": 1, "hearing_impaired": 1 }, "tags": { "language": "ger" } }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/movie.mkv", json);

        var audio = result.AudioStreams[0].Details;
        audio.Title.Should().Be("Commentary");
        audio.Language.Should().Be("eng");
        audio.Profile.Should().Be("DTS-HD MA");
        audio.BitsPerRawSample.Should().Be(24);
        audio.DurationSeconds.Should().Be(12.5);
        audio.Disposition.Should().Be(new StreamDisposition { Default = true, VisualImpaired = true, Comment = true, Original = true });
        audio.Tags["bps-eng"].Should().Be("1509000", "tag lookups ignore case");

        var subtitle = result.SubtitleStreams[0].Details;
        subtitle.Disposition.Forced.Should().BeTrue();
        subtitle.Disposition.HearingImpaired.Should().BeTrue();
        subtitle.Language.Should().Be("ger");
    }

    [Fact]
    public void ParseMediaInfo_ReadsChaptersTitleAndSize()
    {
        var json = """
        {
            "format": { "size": "45000000000", "tags": { "TITLE": "Interstellar" } },
            "chapters": [
                { "id": 1, "start_time": "0.000000", "end_time": "300.500000", "tags": { "title": "Opening" } },
                { "id": 2, "start_time": "300.500000", "end_time": "600.000000" }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/movie.mkv", json);

        result.Title.Should().Be("Interstellar");
        result.SizeBytes.Should().Be(45_000_000_000);
        result.Chapters.Should().Equal(new ChapterInfo(0, 300.5, "Opening"), new ChapterInfo(300.5, 600, null));
    }

    [Fact]
    public void ParseMediaInfo_ReadsDolbyVisionAndFontAttachments()
    {
        var json = """
        {
            "streams": [
                { "index": 0, "codec_type": "video", "codec_name": "hevc",
                  "side_data_list": [ { "side_data_type": "DOVI configuration record", "dv_profile": 8 } ] },
                { "index": 1, "codec_type": "attachment", "codec_name": "ttf",
                  "tags": { "filename": "Arial.ttf", "mimetype": "application/x-truetype-font" } }
            ]
        }
        """;

        var result = MediaProbeService.ParseMediaInfo("/test/movie.mkv", json);

        result.VideoStreams[0].HasDolbyVision.Should().BeTrue();
        result.DataStreams.Should().Equal(new DataStreamInfo(1, "ttf")
        {
            IsAttachment = true,
            FileName = "Arial.ttf",
            MimeType = "application/x-truetype-font",
        });
    }
}
