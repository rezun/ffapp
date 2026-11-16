using System.Collections.Immutable;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Infrastructure;
using FFApp.Tests.Builders;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.Core.Validation;

public sealed class FfmpegFeatureValidationTests
{
    private static readonly FfmpegCapabilities Empty = new(
        ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty);

    [Theory]
    [InlineData(VideoMode.Copy, false)]
    [InlineData(VideoMode.Reencode, true)]
    public void MissingEncoder_OnlyBlocksReencoding(VideoMode mode, bool error)
    {
        var service = new FfmpegCapabilityService { Capabilities = Empty };
        var result = new ValidationService(service).Validate(new OutputSettings(VideoMode: mode), new TestMediaInfoBuilder().Build());
        result.Issues.Any(issue => issue.Code == "ffmpeg_encoder_missing").Should().Be(error);
    }

    [Theory]
    [InlineData(ToneMapEngine.Standard, "zscale")]
    [InlineData(ToneMapEngine.Libplacebo, "libplacebo")]
    public void HdrConversion_ReportsMissingFiltersForTheChosenEngine(ToneMapEngine engine, string missing)
    {
        var media = new TestMediaInfoBuilder().WithVideoStreams(new VideoStreamInfo(0, "hevc", Width: 1920, Height: 1080,
            ColorPrimaries: "bt2020", ColorTransfer: "smpte2084")).Build();
        var available = Empty with
        {
            Encoders = ImmutableHashSet.Create("libx264"),
            Filters = ImmutableHashSet.Create("scale", "format", "setparams", "tonemap"),
        };
        var settings = new OutputSettings(HdrMode: HdrMode.TonemapToSdr, ToneMapSettings: new(Engine: engine));
        var errors = FfmpegFeatureValidation.Validate(settings, media, available);
        errors.Should().ContainSingle(issue => issue.Code == "ffmpeg_filter_missing" && issue.Message.Contains(missing, StringComparison.Ordinal));
    }

    [Fact]
    public async Task QueuedJob_WithMissingEncoder_FailsBeforeStartingAProcess()
    {
        var runner = Substitute.For<IProcessRunner>();
        var capabilities = new FfmpegCapabilityService { Capabilities = Empty };
        var execution = new DefaultJobExecutionService(new DefaultCommandBuilder(new ExecutablePathsConfig()), runner,
            Substitute.For<IMatroskaPostProcessor>(), TimeProvider.System, capabilities);
        var execute = () => execution.ExecuteAsync(new JobDefinition("input.mp4", "output.mp4", OutputContainer.Mp4), new TestMediaInfoBuilder().Build());
        await execute.Should().ThrowAsync<CommandBuildException>().WithMessage("*libx264*");
        runner.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void VideoCopy_DoesNotRequireMissingAudioEncoderWhenThereAreNoAudioTracks()
    {
        var settings = new OutputSettings(VideoMode: VideoMode.Copy, AudioOptions: new(AudioMode.Reencode, AudioCodec.Opus));
        FfmpegFeatureValidation.Validate(settings, new TestMediaInfoBuilder().WithNoAudio().Build(), Empty).Should().BeEmpty();
    }
}
