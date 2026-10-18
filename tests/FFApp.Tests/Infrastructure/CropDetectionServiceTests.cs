using System.Collections.Immutable;

using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class CropDetectionServiceTests
{
    [Fact]
    public async Task DetectAsync_UsesCurrentPositionSelectedVideoStreamAndNormalizedThreshold()
    {
        var runner = new StubProcessRunner
        {
            StderrOutput = "[Parsed_cropdetect_0] crop=1920:800:0:140",
        };
        var paths = new ExecutablePathsConfig();
        paths.Configure(new ExecutablePaths("/tools/ffmpeg", "/tools/ffprobe"));
        var sut = new CropDetectionService(paths, runner);

        var result = await sut.DetectAsync(CreateMediaInfo(), TimeSpan.FromMilliseconds(123_456));

        result.Should().Be(new CropRect(0, 140, 1920, 800));
        runner.Command.Should().NotBeNull();
        runner.Command!.ExecutablePath.Should().Be("/tools/ffmpeg");
        runner.Command.Arguments.Should().ContainInOrder(
            "-ss", "123.456",
            "-i", "/videos/movie.mkv",
            "-map", "0:2",
            "-frames:v", "1",
            "-vf", "cropdetect=limit=0.0941176:round=2:reset=0:skip=0");
    }

    [Fact]
    public async Task DetectAsync_WhenMultipleResultsExist_ReturnsLastResult()
    {
        var runner = new StubProcessRunner
        {
            StderrOutput = "crop=1920:804:0:138\ncrop=1920:800:0:140",
        };
        var sut = new CropDetectionService(new ExecutablePathsConfig(), runner);

        var result = await sut.DetectAsync(CreateMediaInfo(), TimeSpan.Zero);

        result.Should().Be(new CropRect(0, 140, 1920, 800));
    }

    [Fact]
    public async Task DetectAsync_WhenNoCropResultExists_ThrowsHelpfulError()
    {
        var runner = new StubProcessRunner { StderrOutput = "frame=1" };
        var sut = new CropDetectionService(new ExecutablePathsConfig(), runner);

        var act = () => sut.DetectAsync(CreateMediaInfo(), TimeSpan.Zero);

        await act.Should().ThrowAsync<CropDetectionException>()
            .WithMessage("No crop area was detected in the current frame.");
    }

    [Fact]
    public void ParseCrop_IgnoresInvalidResultAndUsesEarlierValidResult()
    {
        const string output = "crop=1280:720:4:8\ncrop=0:0:0:0";

        CropDetectionService.ParseCrop(output)
            .Should().Be(new CropRect(4, 8, 1280, 720));
    }

    private static SourceMediaInfo CreateMediaInfo() => new()
    {
        SourcePath = "/videos/movie.mkv",
        VideoStreams = ImmutableArray.Create(new VideoStreamInfo(2, Width: 1920, Height: 1080)),
    };

    private sealed class StubProcessRunner : IProcessRunner
    {
        public GeneratedCommand? Command { get; private set; }
        public string StderrOutput { get; init; } = "";

        public Task<ProcessExecutionResult> RunAsync(
            GeneratedCommand command,
            double? expectedDurationSeconds = null,
            int? expectedFrameCount = null,
            Action<ProcessProgress>? progressCallback = null,
            Action<string>? logCallback = null,
            ExecutionPauseState? pauseState = null,
            CancellationToken ct = default)
        {
            Command = command;
            return Task.FromResult(new ProcessExecutionResult(
                command, JobState.Completed, 0, 0, StderrOutput));
        }
    }
}
