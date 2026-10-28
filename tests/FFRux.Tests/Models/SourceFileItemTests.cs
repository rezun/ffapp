using FFApp.Core.Models;
using FFRux.Models;
using FluentAssertions;

namespace FFRux.Tests.Models;

public sealed class SourceFileItemTests
{
    [Fact]
    public void Describe_CountsStreamsByKindAndAddsDuration()
    {
        var info = new SourceMediaInfo
        {
            SourcePath = "/media/movie.mkv",
            DurationSeconds = 6133.4,
            VideoStreams = [new VideoStreamInfo(0)],
            AudioStreams = [new AudioStreamInfo(1), new AudioStreamInfo(2)],
            SubtitleStreams = [new SubtitleStreamInfo(3), new SubtitleStreamInfo(4), new SubtitleStreamInfo(5)],
            DataStreams = [new DataStreamInfo(6) { IsAttachment = true }],
        };

        SourceFileItem.Describe(info).Should().Be("Streams: 1 video, 2 audio, 3 subtitle, 1 other · 1:42:13");
    }

    [Fact]
    public void Describe_LeavesOutEmptyKindsAndUnknownDuration()
    {
        var info = new SourceMediaInfo
        {
            SourcePath = "/media/song.mka",
            AudioStreams = [new AudioStreamInfo(0)],
        };

        SourceFileItem.Describe(info).Should().Be("Streams: 1 audio");
    }

    [Fact]
    public void Describe_FileWithoutStreams()
    {
        SourceFileItem.Describe(new SourceMediaInfo { SourcePath = "/media/empty.mkv" })
            .Should().Be("No streams");
    }

    [Theory]
    [InlineData(59.6, "1:00")]
    [InlineData(754, "12:34")]
    [InlineData(3600, "1:00:00")]
    [InlineData(36000, "10:00:00")]
    public void FormatDuration_UsesHoursOnlyWhenNeeded(double seconds, string expected)
    {
        SourceFileItem.FormatDuration(seconds).Should().Be(expected);
    }

    [Fact]
    public void FromProbe_UsesTheProbedPath()
    {
        var item = SourceFileItem.FromProbe(new SourceMediaInfo { SourcePath = "/media/movie.mkv" });

        item.FileName.Should().Be("movie.mkv");
        item.IsError.Should().BeFalse();
    }

    [Fact]
    public void Failed_KeepsTheMessage()
    {
        var item = SourceFileItem.Failed("/media/broken.mkv", "ffprobe timed out");

        item.FileName.Should().Be("broken.mkv");
        item.Summary.Should().Be("ffprobe timed out");
        item.IsError.Should().BeTrue();
    }
}
