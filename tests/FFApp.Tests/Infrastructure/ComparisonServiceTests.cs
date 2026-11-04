using System.Text.Json;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Infrastructure;
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
    public async Task CompletedClip_IsReusedAcrossRenames_ButChangedSettingsOrDeletedClipAreNot()
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
        _service.CacheKey(session, _context.MediaInfo, sample with { Name = "Renamed" }, variant with { Name = "New name" })
            .Should().Be(first.CacheKey);
        File.Delete(first.Path);
        (await _service.EncodeAsync(session, _context.MediaInfo, sample, variant)).WasCached.Should().BeFalse();
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
    public async Task ClearCurrentClips_PreservesConfigurationAndOtherComparisons_AndAllowsReencoding()
    {
        var session = await CreateSavedClipAsync(_context);
        var otherPath = Path.Combine(_directory, "other.mkv");
        File.WriteAllText(otherPath, "other-video");
        var otherContext = _context with { MediaInfo = _context.MediaInfo with { SourcePath = otherPath } };
        var other = await CreateSavedClipAsync(otherContext);
        _service.ProtectSession(session);
        _service.ProtectSession(other);

        var usage = _service.GetCacheUsage(session.SourcePath);
        usage.ComparisonBytes.Should().Be(session.Results[0].SizeBytes);
        usage.ProtectedBytes.Should().Be(usage.TotalBytes);
        _service.ClearClips(session.SourcePath).Should().Be(session.Results[0].SizeBytes);

        File.Exists(_service.ClipPath(session.Results[0].CacheKey)).Should().BeFalse();
        File.Exists(_service.ClipPath(other.Results[0].CacheKey)).Should().BeTrue();
        File.Exists(session.SourcePath).Should().BeTrue();
        (await _service.LoadAsync(_context)).Should().BeEquivalentTo(session);
        (await _service.EncodeAsync(session, _context.MediaInfo, session.Samples[0], session.Variants[0]))
            .WasCached.Should().BeFalse();
    }

    [Fact]
    public async Task ClearAllClips_RemovesOrphansButProtectsOpenComparisons_AndKeepsManifests()
    {
        var session = await CreateSavedClipAsync(_context);
        _service.ProtectSession(session);
        var orphan = WriteCacheFile('a', 25);
        var partial = Path.Combine(_directory, "sessions", "clips", "encoding.partial.mp4");
        File.WriteAllText(partial, "in progress");

        _service.GetCacheUsage().ReclaimableBytes.Should().Be(25);
        _service.ClearClips().Should().Be(25);
        File.Exists(orphan).Should().BeFalse();
        File.Exists(partial).Should().BeTrue();
        File.Exists(_service.ClipPath(session.Results[0].CacheKey)).Should().BeTrue();
        (await _service.LoadAsync(_context)).Should().BeEquivalentTo(session);

        _service.ReleaseSession(session.SourcePath);
        _service.ClearClips().Should().Be(session.Results[0].SizeBytes);
        (await _service.LoadAsync(_context)).Should().BeEquivalentTo(session);
    }

    [Fact]
    public void Limit_RemovesLeastRecentlyUsedClipsFirst_IncludingOrphans()
    {
        var oldest = WriteCacheFile('a', 10);
        var middle = WriteCacheFile('b', 10);
        var newest = WriteCacheFile('c', 10);
        File.SetLastWriteTimeUtc(oldest, new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime);
        File.SetLastWriteTimeUtc(middle, new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime);
        File.SetLastWriteTimeUtc(newest, new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime);

        _service.SetCacheLimit(20);
        File.Exists(oldest).Should().BeFalse();
        File.Exists(middle).Should().BeTrue();
        File.Exists(newest).Should().BeTrue();
        _service.GetCacheUsage().TotalBytes.Should().Be(20);
    }

    [Fact]
    public async Task Limit_ProtectsOpenSessionAndNewEncodes_UntilReleased()
    {
        var session = await _service.LoadAsync(_context);
        _service.ProtectSession(session);
        _service.SetCacheLimit(0);
        var clip = await _service.EncodeAsync(session, _context.MediaInfo, session.Samples[0], session.Variants[0]);
        File.Exists(clip.Path).Should().BeTrue();
        _service.GetCacheUsage().ProtectedBytes.Should().Be(clip.SizeBytes);
        _service.ClearClips().Should().Be(0);
        _service.ReleaseSession(session.SourcePath);
        File.Exists(clip.Path).Should().BeFalse();
        _service.GetCacheUsage().TotalBytes.Should().Be(0);
    }

    [Fact]
    public async Task ReusingClip_RefreshesItsRecency()
    {
        var session = await CreateSavedClipAsync(_context);
        var path = _service.ClipPath(session.Results[0].CacheKey);
        var oldTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcDateTime;
        File.SetLastWriteTimeUtc(path, oldTime);
        await _service.EncodeAsync(session, _context.MediaInfo, session.Samples[0], session.Variants[0]);
        File.GetLastWriteTimeUtc(path).Should().BeAfter(oldTime);
    }

    [Fact]
    public async Task DeleteComparison_RemovesManifestAndHistoricalClips_WithoutTouchingSource()
    {
        var session = await CreateSavedClipAsync(_context);
        var oldKey = session.Results[0].CacheKey;
        var variant = session.Variants[0] with { Settings = session.Variants[0].Settings with { TargetQuality = new Av1QualityOptions(Crf: 32) } };
        var clip = await _service.EncodeAsync(session, _context.MediaInfo, session.Samples[0], variant);
        session = session with { Variants = [variant], Results = [.. session.Results,
            new ComparisonResult(session.Samples[0].Id, variant.Id, clip.CacheKey, clip.SizeBytes, clip.ElapsedSeconds)] };
        await _service.SaveAsync(session);
        _service.ProtectSession(session);
        _service.DeleteComparison(session.SourcePath);

        File.Exists(clip.Path).Should().BeFalse();
        File.Exists(_service.ClipPath(oldKey)).Should().BeFalse();
        File.Exists(session.SourcePath).Should().BeTrue();
        Directory.GetFiles(Path.Combine(_directory, "sessions"), "*.json").Should().BeEmpty();
        (await _service.LoadAsync(_context)).Results.Should().BeEmpty();
    }

    private async Task<ComparisonSession> CreateSavedClipAsync(JobContext context)
    {
        var session = await _service.LoadAsync(context);
        var clip = await _service.EncodeAsync(session, context.MediaInfo, session.Samples[0], session.Variants[0]);
        session = session with { Results = [new ComparisonResult(session.Samples[0].Id, session.Variants[0].Id,
            clip.CacheKey, clip.SizeBytes, clip.ElapsedSeconds)] };
        await _service.SaveAsync(session);
        return session;
    }

    private string WriteCacheFile(char digit, int bytes)
    {
        var path = _service.ClipPath(new string(digit, 64) + ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? throw new InvalidOperationException());
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
