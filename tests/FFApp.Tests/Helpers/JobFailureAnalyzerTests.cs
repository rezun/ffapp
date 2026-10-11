using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Helpers;

public sealed class JobFailureAnalyzerTests
{
    private static readonly GeneratedCommand Ffmpeg = new("ffmpeg", [], "ffmpeg");

    [Fact]
    public void Analyze_OpusFailureAmongWarningsAndShutdownNoise_ReportsSampleRate()
    {
        var warnings = string.Join('\n', Enumerable.Range(15, 29).Select(index =>
            $"[in#0/matroska,webm @ 0x123] Could not find codec parameters for stream {index} (Subtitle: hdmv_pgs_subtitle): unspecified size\nConsider increasing the value for the 'analyzeduration' and 'probesize' options"));
        var metadata = string.Join('\n', Enumerable.Range(0, 44).Select(index =>
            $"Stream #0:{index}\n    Metadata:\n      NUMBER_OF_FRAMES: 5144\n      NUMBER_OF_BYTES: 55751171"));
        var output = warnings + metadata + "\n" + """
            [libopus @ 0x123] Specified sample rate 44100 is not supported by the libopus encoder
            [libopus @ 0x123] Supported sample rates:
            [libopus @ 0x123]   48000
            [libopus @ 0x123]   24000
            [aost#0:1/libopus @ 0x123] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.
            [af#0:1 @ 0x123] Error sending frames to consumers: Invalid argument
            [libopus @ 0x456] Specified sample rate 44100 is not supported by the libopus encoder
            [af#0:1 @ 0x123] Task finished with error code: -22 (Invalid argument)
            Svt[info]: SVT-AV1 Encoder Lib
            Svt[warn]: SVT-AV1 will not insert a key frame at scene changes
            [aost#0:1/libopus @ 0x123] Could not open encoder before EOF
            [out#0/webm @ 0x123] Nothing was written into output file, because at least one of its streams received no packets.
            frame=    0 fps=0.0 q=26.0 Lsize=       0KiB
            Conversion failed!
            """;

        var failure = JobFailureAnalyzer.Analyze(Result(output));

        failure.Title.Should().Be("Unsupported audio sample rate");
        failure.Message.Should().Be("libopus cannot encode audio at 44.1 kHz.");
        failure.Suggestion.Should().Contain("48 kHz").And.Contain("new job");
        failure.Context.Should().Contain("Exit code 234");
        failure.TechnicalDetails.Should().Contain("Specified sample rate 44100")
            .And.NotContain("Subtitle").And.NotContain("Conversion failed");
        failure.TechnicalDetails.Split('\n').Should().ContainSingle();
    }

    [Fact]
    public void Analyze_OtherAudioEncoder_DoesNotAssumeOpusSampleRates()
    {
        var failure = JobFailureAnalyzer.Analyze(Result(
            "[aac @ 0x123] Specified sample rate 12345 is not supported by the aac encoder"));

        failure.Message.Should().Be("aac cannot encode audio at 12.345 kHz.");
        failure.Suggestion.Should().Contain("supported").And.NotContain("48 kHz");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("99999999999999999999")]
    public void Analyze_InvalidSampleRateInLog_FallsBackWithoutThrowing(string rate)
    {
        var line = $"Specified sample rate {rate} is not supported by the libopus encoder";
        var failure = JobFailureAnalyzer.Analyze(Result(line));

        failure.Message.Should().Be(line);
        failure.Title.Should().Be("Encoding failed");
    }

    [Theory]
    [InlineData("No space left on device", "no free space", "Free space")]
    [InlineData("Permission denied", "permission", "permissions")]
    [InlineData("Read-only file system", "read-only", "writable")]
    [InlineData("Unknown encoder 'libsvtav1'", "encoder is unavailable", "FFmpeg build")]
    [InlineData("No such file or directory", "could not be found", "connected")]
    [InlineData("Invalid data found when processing input", "valid media", "complete")]
    public void Analyze_KnownCause_ProvidesSpecificGuidance(string error, string cause, string hint)
    {
        var failure = JobFailureAnalyzer.Analyze(Result($"[out#0 @ 0x123] {error}\nConversion failed!"));

        failure.Message.Should().Contain(cause);
        failure.Suggestion.Should().Contain(hint);
        failure.TechnicalDetails.Should().Contain(error).And.NotContain("Conversion failed");
    }

    [Fact]
    public void Analyze_UnknownFailure_PrioritizesConcreteErrorAndDeduplicatesDetails()
    {
        var output = """
            [enc @ 0x123] Error while opening encoder - maybe incorrect parameters.
            [filter @ 0x123] Invalid crop dimensions: 1920 by 0
            [filter @ 0x456] Invalid crop dimensions: 1920 by 0
            [af @ 0x123] Error sending frames to consumers: Invalid argument
            Conversion failed!
            """;

        var failure = JobFailureAnalyzer.Analyze(Result(output));

        failure.Message.Should().Be("Invalid crop dimensions: 1920 by 0");
        failure.TechnicalDetails.Split('\n').Count(line => line.Contains("Invalid crop", StringComparison.Ordinal))
            .Should().Be(1);
    }

    [Fact]
    public void Analyze_WarningsOnly_DoesNotClaimWarningIsCause()
    {
        var failure = JobFailureAnalyzer.Analyze(Result("""
            [in @ 0x123] Could not find codec parameters for stream 15: unspecified size
            Svt[warn]: Warning: invalid setting ignored
            Svt[info]: Error resilience enabled
            """));

        failure.Message.Should().Be("The process stopped without reporting a specific cause.");
    }

    [Fact]
    public void Analyze_EmptyLog_StillShowsExitCode()
    {
        var failure = JobFailureAnalyzer.Analyze(Result(""));

        failure.Message.Should().Contain("without reporting a specific cause");
        failure.Context.Should().Be("ffmpeg · Exit code 234");
        failure.TechnicalDetails.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_FailedSecondPass_IgnoresSuccessfulFirstPass()
    {
        var first = new ProcessExecutionResult(Ffmpeg, JobState.Completed, 0, 1, "Unknown encoder mentioned in a warning");
        var second = new ProcessExecutionResult(Ffmpeg, JobState.Failed, 1, 1, "[out @ 0x123] No space left on device");
        var result = Result("") with { Commands = [Ffmpeg, Ffmpeg], ProcessResults = [first, second] };

        var failure = JobFailureAnalyzer.Analyze(result);

        failure.Message.Should().Contain("no free space");
        failure.Context.Should().Be("ffmpeg · Pass 2/2 · Exit code 1");
    }

    [Fact]
    public void Analyze_PostProcessingFailure_IdentifiesToolAndPhase()
    {
        var tool = new GeneratedCommand("/tools/mkvpropedit", [], "mkvpropedit", ProcessProtocol.Plain);
        var result = Result("") with
        {
            Commands = [Ffmpeg, tool],
            ProcessResults =
            [
                new ProcessExecutionResult(Ffmpeg, JobState.Completed, 0, 1, "Unknown encoder mentioned in a warning"),
                new ProcessExecutionResult(tool, JobState.Failed, 2, 1, "Error: The file is locked."),
            ],
        };

        var failure = JobFailureAnalyzer.Analyze(result);

        failure.Title.Should().Be("Post-processing failed");
        failure.Message.Should().Be("Error: The file is locked.");
        failure.Context.Should().Be("mkvpropedit · Post-processing · Exit code 2");
    }

    [Fact]
    public void Analyze_NoProcessResults_UsesJobLog()
    {
        var failure = JobFailureAnalyzer.Analyze(Result("Permission denied") with { ProcessResults = [] });
        failure.Message.Should().Contain("permission");
    }

    [Fact]
    public void Analyze_StartupException_DisplaysExceptionMessage()
    {
        var failure = JobFailureAnalyzer.Analyze(null, "Could not start /tools/ffmpeg: file missing");

        failure.Message.Should().Be("Could not start /tools/ffmpeg: file missing");
        failure.Context.Should().BeEmpty();
    }

    [Fact]
    public void Analyze_LargeUnknownError_BoundsSummaryAndDetails()
    {
        var output = string.Join('\n', Enumerable.Range(0, 30).Select(index => $"Error {index}: {new string('x', 500)}"));
        var failure = JobFailureAnalyzer.Analyze(Result(output));

        failure.Message.Length.Should().BeLessThanOrEqualTo(241);
        failure.TechnicalDetails.Split('\n').Should().HaveCount(6);
    }

    private static JobResult Result(string output) => new(
        new TestJobDefinitionBuilder().Build(), JobState.Failed, [Ffmpeg],
        [new ProcessExecutionResult(Ffmpeg, JobState.Failed, 234, 1, output)], 1, output);
}
