using System.Text.Json;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Infrastructure;
using FFApp.Infrastructure.Serialization;
using FFApp.Tests.Builders;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.Infrastructure;

public sealed class ComparisonServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ffapp-compare-tests-" + Guid.NewGuid().ToString("N"));
    private readonly IJobExecutionService _execution = Substitute.For<IJobExecutionService>();
    private readonly JsonSerializerOptions _json = new();
    private readonly ComparisonService _service;
    private readonly JobContext _context;

    public ComparisonServiceTests()
    {
        Directory.CreateDirectory(_directory);
        var source = Path.Combine(_directory, "source.mkv");
        File.WriteAllText(source, "source-video");
        var settings = new OutputSettings(VideoCodec: VideoCodec.Av1,
            TargetQuality: new Av1QualityOptions(Crf: 24, FilmGrain: 8, FilmGrainDenoise: 1),
            VideoEncoderOptions: new Av1EncoderOptions(FilmGrain: 8, FilmGrainDenoise: 1));
        _context = new JobContext(new TestMediaInfoBuilder().WithSourcePath(source).WithDuration(120).Build(), settings);
        _service = new ComparisonService(new DefaultCommandBuilder(new ExecutablePathsConfig()), _execution,
            new ValidationService(), _json, Path.Combine(_directory, "sessions"));
        _execution.ExecuteAsync(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>(), Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(), Arg.Any<ExecutionPauseState?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var job = call.Arg<JobDefinition>();
                File.WriteAllText(job.OutputPath, "encoded-clip");
                return Task.FromResult(new JobResult(job, JobState.Completed, [], [], 7.5, ""));
            });
    }

    [Fact]
    public async Task SaveAndReopen_RestoresSamplesVariantsAndInitialSettings()
    {
        var session = await _service.LoadAsync(_context);
        var variant = session.Variants[0] with { Name = "Grain 10", Settings = _context.Settings with
        {
            TargetQuality = new Av1QualityOptions(Crf: 24, FilmGrain: 10, FilmGrainDenoise: 1),
            HdrMode = HdrMode.TonemapToSdr,
            ToneMapSettings = new ToneMapSettings(Engine: ToneMapEngine.Libplacebo, ContrastRecovery: 0.7),
        } };
        session = session with { Variants = [variant], Samples = [new ComparisonSample("manual", "Dark scene", 32, 9)], SelectedAudioStreamIndices = [1, 3] };
        await _service.SaveAsync(session);
        var restored = await _service.LoadAsync(_context with { Settings = new OutputSettings() });
        restored.Should().BeEquivalentTo(session);
        restored.Variants[0].Settings.TargetQuality.Should().Be(variant.Settings.TargetQuality);
    }

    [Fact]
    public async Task SourceReplacement_StartsFreshSession()
    {
        var session = await _service.LoadAsync(_context);
        await _service.SaveAsync(session with { Samples = [new ComparisonSample("old", "Old sample", 10, 3)] });
        File.AppendAllText(_context.MediaInfo.SourcePath, "changed");
        var reloaded = await _service.LoadAsync(_context);
        reloaded.SourceFingerprint.Should().NotBe(session.SourceFingerprint);
        reloaded.Samples.Should().NotContain(s => s.Id == "old");
    }

    [Fact]
    public async Task CompletedClip_IsReused_ButChangedSettingsOrDeletedClipAreNot()
    {
        var session = await _service.LoadAsync(_context);
        var sample = session.Samples[0];
        var variant = session.Variants[0];
        var first = await _service.EncodeAsync(session, _context.MediaInfo, sample, variant);
        first.WasCached.Should().BeFalse();
        first.OffsetSeconds.Should().Be(Math.Min(ComparisonPlan.PreRollSeconds, sample.StartSeconds));
        session = session with { Results = [new ComparisonResult(sample.Id, variant.Id, first.CacheKey, first.SizeBytes, first.ElapsedSeconds)] };
        var second = await _service.EncodeAsync(session, _context.MediaInfo, sample, variant);
        second.WasCached.Should().BeTrue();
        second.ElapsedSeconds.Should().Be(7.5);
        await _execution.Received(1).ExecuteAsync(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>(),
            Arg.Any<Action<JobProgress>?>(), Arg.Any<Action<string>?>(), Arg.Any<ExecutionPauseState?>(), Arg.Any<CancellationToken>());
        var changed = variant with { Settings = variant.Settings with { TargetQuality = new Av1QualityOptions(Crf: 28) } };
        _service.CacheKey(session, _context.MediaInfo, sample, changed).Should().NotBe(first.CacheKey);
        _service.CacheKey(session, _context.MediaInfo, sample with { StartSeconds = sample.StartSeconds + 1 }, variant).Should().NotBe(first.CacheKey);
        File.Delete(first.Path);
        (await _service.EncodeAsync(session, _context.MediaInfo, sample, variant)).WasCached.Should().BeFalse();
    }

    [Fact]
    public async Task RenamingVariantsOrSamples_DoesNotInvalidateIdenticalEncoding()
    {
        var session = await _service.LoadAsync(_context);
        _service.CacheKey(session, _context.MediaInfo, session.Samples[0], session.Variants[0]).Should().Be(
            _service.CacheKey(session, _context.MediaInfo, session.Samples[0] with { Name = "Renamed" }, session.Variants[0] with { Name = "New name" }));
    }

    [Theory]
    [InlineData(JobState.Failed)]
    [InlineData(JobState.Canceled)]
    public async Task InterruptedEncoding_RemovesPartialFiles_AndDoesNotPublishCache(JobState state)
    {
        var session = await _service.LoadAsync(_context);
        _execution.ExecuteAsync(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>(), Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(), Arg.Any<ExecutionPauseState?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var job = call.Arg<JobDefinition>();
                File.WriteAllText(job.OutputPath, "partial");
                return Task.FromResult(new JobResult(job, state, [], [], 1, "encoder failed"));
            });
        var action = () => _service.EncodeAsync(session, _context.MediaInfo, session.Samples[0], session.Variants[0]);
        if (state == JobState.Canceled) await action.Should().ThrowAsync<OperationCanceledException>();
        else await action.Should().ThrowAsync<ComparisonEncodingException>().WithMessage("encoder failed");
        Directory.EnumerateFiles(Path.Combine(_directory, "sessions", "clips")).Should().BeEmpty();
    }

    [Fact]
    public async Task ChangedSource_IsRejectedBeforeEncoding()
    {
        var session = await _service.LoadAsync(_context);
        File.AppendAllText(session.SourcePath, "replacement");
        var action = () => _service.EncodeAsync(session, _context.MediaInfo, session.Samples[0], session.Variants[0]);
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*source has changed*");
    }

    [Theory]
    [InlineData("../../outside.mp4")]
    [InlineData("invalid.mp4")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.exe")]
    public void CachePaths_CannotEscapeComparisonDirectory(string key)
    {
        var action = () => _service.ClipPath(key);
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task MissingOptionalEncoderFields_LoadWithBackwardCompatibleDefaults()
    {
        var session = await _service.LoadAsync(_context);
        var node = ComparisonSerializer.Serialize(session, _json);
        var quality = node["variants"]?[0]?["settings"]?["target_quality"]?.AsObject()
            ?? throw new InvalidOperationException("Missing quality settings.");
        quality.Remove("film_grain");
        quality.Remove("film_grain_denoise");
        using var document = JsonDocument.Parse(node.ToJsonString());
        var result = ComparisonSerializer.Deserialize(document.RootElement, _json);
        result.Variants[0].Settings.TargetQuality.Should().BeOfType<Av1QualityOptions>()
            .Which.FilmGrain.Should().Be(0);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
