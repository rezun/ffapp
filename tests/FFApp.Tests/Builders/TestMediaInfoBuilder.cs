using System.Collections.Immutable;
using FFApp.Core.Models;

namespace FFApp.Tests.Builders;

public class TestMediaInfoBuilder
{
    private string _sourcePath = "/test/source.mp4";
    private readonly string? _containerFormat = "mov,mp4,m4a,3gp,3g2,mj2";
    private double? _durationSeconds = 60.0;
    private readonly int? _bitRate = 5_000_000;
    private ImmutableArray<string> _metadataKeys = [];
    private ImmutableArray<VideoStreamInfo> _videoStreams = [new(0, "h264", 4_500_000, 1920, 1080, 23.976, 1439)];
    private ImmutableArray<AudioStreamInfo> _audioStreams = [new(1, "aac", 128_000, 2, 48000, "eng")];
    private ImmutableArray<SubtitleStreamInfo> _subtitleStreams = [];
    private ImmutableArray<DataStreamInfo> _dataStreams = [];
    private ImmutableArray<AttachedPictureStreamInfo> _attachedPictureStreams = [];

    public SourceMediaInfo Build() => new()
    {
        SourcePath = _sourcePath,
        ContainerFormat = _containerFormat,
        DurationSeconds = _durationSeconds,
        BitRate = _bitRate,
        MetadataKeys = _metadataKeys,
        VideoStreams = _videoStreams,
        AudioStreams = _audioStreams,
        SubtitleStreams = _subtitleStreams,
        DataStreams = _dataStreams,
        AttachedPictureStreams = _attachedPictureStreams,
    };

    public TestMediaInfoBuilder WithSourcePath(string path) { _sourcePath = path; return this; }
    public TestMediaInfoBuilder WithDuration(double? seconds) { _durationSeconds = seconds; return this; }
    public TestMediaInfoBuilder WithNoDuration() => WithDuration(null);
    public TestMediaInfoBuilder WithMetadataKeys(params string[] keys) { _metadataKeys = [.. keys]; return this; }

    public TestMediaInfoBuilder WithAttachedPictures()
    {
        _attachedPictureStreams =
        [
            new AttachedPictureStreamInfo(7, "png", "release.png", "image/png"),
            new AttachedPictureStreamInfo(8, "mjpeg", "cover.jpg", "image/jpeg"),
        ];
        return this;
    }

    public TestMediaInfoBuilder WithVideoStreams(params VideoStreamInfo[] streams)
    {
        _videoStreams = [.. streams];
        return this;
    }

    public TestMediaInfoBuilder WithNoVideo()
    {
        _videoStreams = [];
        return this;
    }

    public TestMediaInfoBuilder WithMultipleVideoStreams() =>
        WithVideoStreams(
            new VideoStreamInfo(0, "h264", 4_500_000, 1920, 1080, 23.976, 1439),
            new VideoStreamInfo(2, "mjpeg", null, 320, 240));

    public TestMediaInfoBuilder WithPortrait() =>
        WithVideoStreams(new VideoStreamInfo(0, "h264", 4_500_000, 1080, 1920, 23.976, 1439));

    public TestMediaInfoBuilder WithRotatedPortrait(int rotation = -90) =>
        WithVideoStreams(new VideoStreamInfo(0, "h264", 4_500_000, 3840, 2160, 23.976, 1439, rotation));

    public TestMediaInfoBuilder WithFrameRate(double? fps)
    {
        var v = _videoStreams.Length > 0 ? _videoStreams[0] : new VideoStreamInfo(0);
        _videoStreams = [v with { FrameRate = fps }];
        return this;
    }

    public TestMediaInfoBuilder WithFrameCount(int? count)
    {
        var v = _videoStreams.Length > 0 ? _videoStreams[0] : new VideoStreamInfo(0);
        _videoStreams = [v with { FrameCount = count }];
        return this;
    }

    public TestMediaInfoBuilder WithVideoMetadataKeys(params string[] keys)
    {
        var video = _videoStreams.Length > 0 ? _videoStreams[0] : new VideoStreamInfo(0);
        _videoStreams = [video with { MetadataKeys = [.. keys] }];
        return this;
    }

    public TestMediaInfoBuilder WithAudioStreams(params AudioStreamInfo[] streams)
    {
        _audioStreams = [.. streams];
        return this;
    }

    public TestMediaInfoBuilder WithNoAudio() => WithAudioStreams();

    public TestMediaInfoBuilder WithAudioBitrate(int? bitrate)
    {
        var a = _audioStreams.Length > 0 ? _audioStreams[0] : new AudioStreamInfo(1, "aac");
        _audioStreams = [a with { BitRate = bitrate }];
        return this;
    }

    public TestMediaInfoBuilder WithAudioMetadataKeys(params string[] keys)
    {
        var audio = _audioStreams.Length > 0 ? _audioStreams[0] : new AudioStreamInfo(1, "aac");
        _audioStreams = [audio with { MetadataKeys = [.. keys] }];
        return this;
    }

    public TestMediaInfoBuilder WithSubtitles(string codecName = "subrip", string? language = "eng")
    {
        _subtitleStreams = [new(2, codecName, language)];
        return this;
    }

    public TestMediaInfoBuilder WithDataStreams(string codecName = "bin_data")
    {
        _dataStreams = [new(3, codecName)];
        return this;
    }

    public TestMediaInfoBuilder WithHdr10(int? maxCll = 1000, int? maxFall = 400) =>
        WithVideoStreams(new VideoStreamInfo(0, "hevc", 20_000_000, 3840, 2160, 23.976, 1439,
            PixelFormat: "yuv420p10le",
            ColorPrimaries: "bt2020",
            ColorTransfer: "smpte2084",
            ColorSpace: "bt2020nc",
            ColorRange: "tv",
            BitsPerRawSample: 10,
            HdrMetadata: new HdrMetadata(maxCll, maxFall, null)));

    public TestMediaInfoBuilder WithHdr10AndMasteringDisplay() =>
        WithVideoStreams(new VideoStreamInfo(0, "hevc", 20_000_000, 3840, 2160, 23.976, 1439,
            PixelFormat: "yuv420p10le",
            ColorPrimaries: "bt2020",
            ColorTransfer: "smpte2084",
            ColorSpace: "bt2020nc",
            ColorRange: "tv",
            BitsPerRawSample: 10,
            HdrMetadata: new HdrMetadata(1000, 400,
                new MasteringDisplayMetadata(
                    RedX: "13250", RedY: "34500",
                    GreenX: "7500", GreenY: "3000",
                    BlueX: "34000", BlueY: "16000",
                    WhitePointX: "15635", WhitePointY: "16450",
                    MinLuminance: "50", MaxLuminance: "10000000"))));

    public TestMediaInfoBuilder WithHlg() =>
        WithVideoStreams(new VideoStreamInfo(0, "hevc", 20_000_000, 3840, 2160, 50.0, 3000,
            PixelFormat: "yuv420p10le",
            ColorPrimaries: "bt2020",
            ColorTransfer: "arib-std-b67",
            ColorSpace: "bt2020nc",
            ColorRange: "tv",
            BitsPerRawSample: 10));
}
