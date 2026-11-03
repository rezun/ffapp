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

    [Fact]
    public async Task SuggestAsync_SamplesAcrossSourceAndKeepsAreaVisibleInAnySample()
    {
        var runner = new StubProcessRunner
        {
            StderrOutputs = new Queue<string>(
            [
                "crop=1920:800:0:140",
                // A dark scene collapses toward the center and must not shrink the result.
                "crop=1200:600:360:240",
                "crop=1920:804:0:138",
                "crop=1920:800:0:140",
                "frame=1",
                "crop=1920:800:0:140",
            ]),
        };
        var sut = new CropDetectionService(new ExecutablePathsConfig(), runner);

        var result = await sut.SuggestAsync(CreateMediaInfo(durationSeconds: 7000));

        result.Should().Be(new CropRect(0, 138, 1920, 804));
        runner.Commands.Select(command => command.Arguments[command.Arguments.IndexOf("-ss") + 1])
            .Should().Equal("1000", "2000", "3000", "4000", "5000", "6000");
        runner.Commands.Should().AllSatisfy(command =>
            command.Arguments.Should().ContainInOrder("-frames:v", "3"));
    }

    [Theory]
    [InlineData("crop=1920:1080:0:0")]
    [InlineData("crop=1916:1076:2:2")]
    [InlineData("crop=800:400:560:340")]
    public async Task SuggestAsync_WhenCropIsNegligibleOrImplausible_ReturnsNull(string output)
    {
        var runner = new StubProcessRunner { StderrOutput = output };
        var sut = new CropDetectionService(new ExecutablePathsConfig(), runner);

        var result = await sut.SuggestAsync(CreateMediaInfo(durationSeconds: 7000));

        result.Should().BeNull();
    }

    [Fact]
    public async Task SuggestAsync_WhenMostSamplesFail_ReturnsNull()
    {
        var runner = new StubProcessRunner
        {
            StderrOutputs = new Queue<string>(
                ["crop=1920:800:0:140", "crop=1920:800:0:140", "frame=1", "frame=1", "frame=1", "frame=1"]),
        };
        var sut = new CropDetectionService(new ExecutablePathsConfig(), runner);

        var result = await sut.SuggestAsync(CreateMediaInfo(durationSeconds: 7000));

        result.Should().BeNull();
    }

    [Fact]
    public async Task SuggestAsync_WithoutDuration_DoesNotRunFfmpeg()
    {
        var runner = new StubProcessRunner();
        var sut = new CropDetectionService(new ExecutablePathsConfig(), runner);

        var result = await sut.SuggestAsync(CreateMediaInfo(durationSeconds: null));

        result.Should().BeNull();
        runner.Commands.Should().BeEmpty();
    }

    private static SourceMediaInfo CreateMediaInfo(double? durationSeconds = null) => new()
    {
        SourcePath = "/videos/movie.mkv",
        DurationSeconds = durationSeconds,
        VideoStreams = ImmutableArray.Create(new VideoStreamInfo(2, Width: 1920, Height: 1080)),
    };

    private sealed class StubProcessRunner : IProcessRunner
    {
        public GeneratedCommand? Command => Commands.LastOrDefault();
        public List<GeneratedCommand> Commands { get; } = [];
        public string StderrOutput { get; init; } = "";
        public Queue<string>? StderrOutputs { get; init; }

        public Task<ProcessExecutionResult> RunAsync(
            GeneratedCommand command,
            double? expectedDurationSeconds = null,
            int? expectedFrameCount = null,
            Action<ProcessProgress>? progressCallback = null,
            Action<string>? logCallback = null,
            ExecutionPauseState? pauseState = null,
            CancellationToken ct = default)
        {
            Commands.Add(command);
            var output = StderrOutputs is { Count: > 0 } outputs ? outputs.Dequeue() : StderrOutput;
            return Task.FromResult(new ProcessExecutionResult(
                command, JobState.Completed, 0, 0, output));
        }
    }
}
