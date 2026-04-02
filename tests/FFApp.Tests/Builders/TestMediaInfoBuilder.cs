using System.Collections.Immutable;
using FFApp.Core.Models;

namespace FFApp.Tests.Builders;

public class TestMediaInfoBuilder
{
    private string _sourcePath = "/test/source.mp4";
    private string? _containerFormat = "mov,mp4,m4a,3gp,3g2,mj2";
    private double? _durationSeconds = 60.0;
    private int? _bitRate = 5_000_000;
    private ImmutableArray<VideoStreamInfo> _videoStreams = [new(0, "h264", 4_500_000, 1920, 1080, 23.976, 1439)];
    private ImmutableArray<AudioStreamInfo> _audioStreams = [new(1, "aac", 128_000, 2, 48000, "eng")];
    private ImmutableArray<SubtitleStreamInfo> _subtitleStreams = [];
    private ImmutableArray<DataStreamInfo> _dataStreams = [];

    public SourceMediaInfo Build() => new()
    {
        SourcePath = _sourcePath,
        ContainerFormat = _containerFormat,
        DurationSeconds = _durationSeconds,
        BitRate = _bitRate,
        VideoStreams = _videoStreams,
        AudioStreams = _audioStreams,
        SubtitleStreams = _subtitleStreams,
        DataStreams = _dataStreams,
    };

    public TestMediaInfoBuilder WithSourcePath(string path) { _sourcePath = path; return this; }
    public TestMediaInfoBuilder WithDuration(double? seconds) { _durationSeconds = seconds; return this; }
    public TestMediaInfoBuilder WithNoDuration() => WithDuration(null);
    public TestMediaInfoBuilder WithBitRate(int? bitRate) { _bitRate = bitRate; return this; }
    public TestMediaInfoBuilder WithContainerFormat(string? format) { _containerFormat = format; return this; }

    public TestMediaInfoBuilder WithVideoStreams(params VideoStreamInfo[] streams)
    {
        _videoStreams = [..streams];
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

    public TestMediaInfoBuilder WithVideoResolution(int width, int height)
    {
        var v = _videoStreams.Length > 0 ? _videoStreams[0] : new VideoStreamInfo(0);
        _videoStreams = [v with { Width = width, Height = height }];
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
}
