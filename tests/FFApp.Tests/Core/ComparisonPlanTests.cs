using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Core;

public sealed class ComparisonPlanTests
{
    [Fact]
    public void Samples_AreCenteredAcrossTrimRange_WithoutOverlap()
    {
        var samples = ComparisonPlan.Distribute(100, 900, 8, 12);
        samples.Should().HaveCount(8);
        samples[0].StartSeconds.Should().Be(144);
        samples[^1].EndSeconds.Should().Be(856);
        samples.Should().OnlyContain(s => ComparisonPlan.IsValid(s, 100, 900));
        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(3, 1, 3)]
    [InlineData(24, 2, 12)]
    [InlineData(25, 2, 12)]
    public void ShortSources_ClampSampleCountAndDuration(double length, int expectedCount, double expectedLength)
    {
        var samples = ComparisonPlan.Distribute(10, 10 + length, 8, 12);
        samples.Should().HaveCount(expectedCount);
        samples.Should().OnlyContain(s => s.DurationSeconds == expectedLength && ComparisonPlan.IsValid(s, 10, 10 + length));
    }

    [Theory]
    [InlineData(double.NaN, 60, 8, 12)]
    [InlineData(0, double.PositiveInfinity, 8, 12)]
    [InlineData(20, 10, 8, 12)]
    [InlineData(0, 60, 0, 12)]
    [InlineData(0, 60, 25, 12)]
    [InlineData(0, 60, 8, 0)]
    public void InvalidSamplingRequests_AreRejected(double start, double end, int count, double length)
    {
        var action = () => ComparisonPlan.Distribute(start, end, count, length);
        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    internal static ComparisonSession Session(OutputSettings? settings = null) => new(
        "/source.mkv", "source-version", settings ?? new OutputSettings(), 100, 900,
        new CropRect(0, 140, 1920, 800), [], [], []);

    [Fact]
    public void Clip_UsesSameEncoderHdrAndGeometry_ButExcludesOtherStreams()
    {
        var settings = new OutputSettings(VideoCodec: VideoCodec.Av1,
            TargetQuality: new Av1QualityOptions(Crf: 24, FilmGrain: 8, FilmGrainDenoise: 1),
            HdrMode: HdrMode.TonemapToSdr, ToneMapSettings: new ToneMapSettings(Algorithm: ToneMapAlgorithm.Mobius),
            KeyframeInterval: 120, ColorMetadata: new ColorMetadataSettings());
        var sample = new ComparisonSample("s", "Sample", 200, 12);
        var session = Session(settings);
        var media = new TestMediaInfoBuilder().WithHdr10().WithDuration(1000).Build();
        var job = ComparisonPlan.BuildJob(session, media, sample, new ComparisonVariant("v", "Grain", settings), "/test/clip.mp4");
        job.TrimStartSeconds.Should().Be(198);
        job.TrimEndSeconds.Should().Be(212);
        job.AudioOptions.Mode.Should().Be(AudioMode.Remove);
        job.StreamHandling.Should().Be(new StreamHandlingOptions(true, true, true));
        job.Crop.Should().Be(session.Crop);
        job.ToneMapSettings.Should().Be(settings.ToneMapSettings);
        job.ColorMetadata.Should().Be(settings.ColorMetadata);
        job.KeyframeInterval.Should().Be(120);
        var command = new DefaultCommandBuilder(new ExecutablePathsConfig()).Build(job, media).Single();
        command.Arguments.Should().Contain("-0:a?");
        command.Arguments.Should().Contain(a => a.Contains("film-grain=8:film-grain-denoise=1", StringComparison.Ordinal));
    }

    [Fact]
    public void PreRoll_DoesNotReachOutsideSavedTrim()
    {
        var settings = new OutputSettings();
        var job = ComparisonPlan.BuildJob(Session(), new TestMediaInfoBuilder().WithDuration(1000).Build(),
            new ComparisonSample("s", "Sample", 100.5, 12), new ComparisonVariant("v", "Baseline", settings), "/clip.mp4");
        job.TrimStartSeconds.Should().Be(100);
        job.TrimEndSeconds.Should().Be(112.5);
    }

    [Fact]
    public void FileSizeTarget_UsesWholeMovieBudgetAndSelectedAudio_NotShortClipDuration()
    {
        var settings = new OutputSettings(EncodingStrategy: EncodingStrategy.TargetFileSize,
            TargetFileSize: new TargetFileSizeOptions(500, TwoPass: true));
        var session = Session(settings) with { SelectedAudioStreamIndices = [1] };
        var media = new TestMediaInfoBuilder().WithDuration(1000).Build();
        var full = FormatHelpers.BuildJobDefinition(session.SourcePath, "/movie.mp4", settings,
            session.RangeStart, session.RangeEnd, session.Crop, session.SelectedAudioStreamIndices);
        var clip = ComparisonPlan.BuildJob(session, media, new ComparisonSample("s", "Sample", 200, 12),
            new ComparisonVariant("v", "Size target", settings), "/clip.mp4");
        clip.EncodingStrategy.Should().Be(EncodingStrategy.TargetBitrate);
        clip.TargetBitrate?.VideoBitrate.Should().Be(DefaultCommandBuilder.ResolveVideoBitrate(full, media));
        clip.TargetBitrate?.TwoPass.Should().BeTrue();
    }

    [Theory]
    [InlineData(99, 12)]
    [InlineData(899, 12)]
    [InlineData(200, 0)]
    [InlineData(double.NaN, 12)]
    public void InvalidClipRange_CannotCreateJob(double start, double duration)
    {
        var action = () => ComparisonPlan.BuildJob(Session(), new TestMediaInfoBuilder().Build(),
            new ComparisonSample("s", "Bad", start, duration), new ComparisonVariant("v", "Baseline", new OutputSettings()), "/clip.mp4");
        action.Should().Throw<InvalidOperationException>();
    }
}
