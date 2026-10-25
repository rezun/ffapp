using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class ComparisonFramePreviewTests
{
    [Fact]
    public void FrameStep_WithCacheHit_PublishesBothFramesBeforeCommandReturns_WithoutDecode()
    {
        var frames = new CachedFrameService();
        using var vm = new ComparisonPlaybackViewModel(null, frames);
        vm.Load(new ComparisonTrack("left", 100), new ComparisonTrack("right", 2), 12, 24);

        vm.AdvanceFrame(() => { }, null);

        vm.FramesToDisplay.Should().BeEquivalentTo(new ComparisonFrames([1], [2]));
        frames.Prefetches.Should().Be(2);
    }

    [Fact]
    public void FrameStep_WithoutCache_StartsDecodingImmediately_WithoutScrubberDelay()
    {
        var decodes = 0;
        var frames = new FrameService((_, _) => { decodes++; return Task.FromResult<byte[]>([1]); });
        using var vm = new ComparisonPlaybackViewModel(null, frames);
        vm.Load(new ComparisonTrack("left", 100), new ComparisonTrack("right", 2), 12, 24);

        vm.AdvanceFrame(() => { }, null);

        decodes.Should().Be(2);
        vm.FramesToDisplay.Should().NotBeNull();
    }

    [Fact]
    public async Task FrameStep_WithStillFrameBackend_DecodesNewPositionWithoutAdvancingVlc()
    {
        var frames = new FrameService((_, _) => Task.FromResult<byte[]>([1]));
        using var vm = new ComparisonPlaybackViewModel(null, frames);
        vm.Load(new ComparisonTrack("left", 100), new ComparisonTrack("right", 2), 12, 24);
        var nativeSteps = 0;

        vm.AdvanceFrame(() => nativeSteps++, () => nativeSteps++);
        await vm.RefreshFramesAsync();

        nativeSteps.Should().Be(0);
        vm.PositionSeconds.Should().Be(1.0 / 24);
        vm.FramesToDisplay.Should().BeEquivalentTo(new ComparisonFrames([1], [1]));
    }

    [Fact]
    public async Task ScrubbingWhilePlaying_PausesBothPreviews_AndLoadsTheSelectedFramePair()
    {
        var frames = new FrameService((_, _) => Task.FromResult<byte[]>([1]));
        using var vm = new ComparisonPlaybackViewModel(null, frames);
        vm.Load(new ComparisonTrack("left", 100), new ComparisonTrack("right", 2), 12, 24);
        vm.IsPlaying = true;

        vm.PositionSeconds = 1.821;
        await vm.RefreshFramesAsync();

        vm.IsPlaying.Should().BeFalse();
        vm.PositionSeconds.Should().Be(1.821);
        vm.FramesToDisplay.Should().BeEquivalentTo(new ComparisonFrames([1], [1]));
    }

    [Theory]
    [InlineData(24, 1684.656, 2, 1.821)]
    [InlineData(24000.0 / 1001, 1684.656, 2, 1.821)]
    [InlineData(60, 100.001, 2, 0)]
    [InlineData(25, 100, 0, 99)]
    public void FrameTime_MapsSourceAndTrimmedClipToTheSameSourceFrame(
        double fps, double start, double preroll, double position)
    {
        var source = new ComparisonTrack("source", start);
        var clip = new ComparisonTrack("clip", preroll, SourceStartSeconds: start - preroll);
        var sourceTime = ComparisonPlaybackViewModel.FrameTime(source, position, 12, fps);
        var clipTime = ComparisonPlaybackViewModel.FrameTime(clip, position, 12, fps);
        var sourceFrame = Math.Ceiling(sourceTime * fps);
        var clipFrame = Math.Ceiling(clipTime * fps);
        (clipFrame + Math.Ceiling((start - preroll) * fps - 1e-7)).Should().Be(sourceFrame);
    }

    [Fact]
    public async Task Preview_PublishesBothFramesTogether_AfterTheSlowerDecoderCompletes()
    {
        var left = new TaskCompletionSource<byte[]>();
        var right = new TaskCompletionSource<byte[]>();
        var started = new TaskCompletionSource();
        var frames = new FrameService((path, _) =>
        {
            if (path == "left") return left.Task;
            started.SetResult();
            return right.Task;
        });
        using var vm = new ComparisonPlaybackViewModel(null, frames);
        vm.Load(new ComparisonTrack("left", 100), new ComparisonTrack("right", 2), 12, 24);
        var pending = vm.RefreshFramesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        right.SetResult([2]);
        vm.FramesToDisplay.Should().BeNull();

        left.SetResult([1]);
        await pending;

        vm.FramesToDisplay.Should().BeEquivalentTo(new ComparisonFrames([1], [2]));
    }

    [Fact]
    public async Task Scrubbing_CancelsPreviousDecode_AndLateResultsCannotReplaceTheLatestPair()
    {
        var oldLeft = new TaskCompletionSource<byte[]>();
        var oldRight = new TaskCompletionSource<byte[]>();
        var started = new TaskCompletionSource();
        var oldToken = CancellationToken.None;
        var useOld = true;
        var frames = new FrameService((path, ct) =>
        {
            if (!useOld) return Task.FromResult<byte[]>([9]);
            oldToken = ct;
            if (path == "left") return oldLeft.Task;
            started.SetResult();
            return oldRight.Task;
        });
        using var vm = new ComparisonPlaybackViewModel(null, frames);
        vm.Load(new ComparisonTrack("left", 100), new ComparisonTrack("right", 2), 12, 24);
        var old = vm.RefreshFramesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        useOld = false;

        await vm.RefreshFramesAsync();
        oldToken.IsCancellationRequested.Should().BeTrue();
        oldLeft.SetResult([1]);
        oldRight.SetResult([2]);
        await old;

        vm.FramesToDisplay.Should().BeEquivalentTo(new ComparisonFrames([9], [9]));
    }

    [Fact]
    public async Task Reset_DiscardsPendingFrames_WhenTheSampleChanges()
    {
        var decoded = new TaskCompletionSource<byte[]>();
        var started = new TaskCompletionSource();
        var frames = new FrameService((_, _) => { started.TrySetResult(); return decoded.Task; });
        using var vm = new ComparisonPlaybackViewModel(null, frames);
        vm.LoadSource(new ComparisonTrack("left", 0), 12, 24);
        var pending = vm.RefreshFramesAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        vm.Reset();
        decoded.SetResult([1]);
        await pending;
        vm.FramesToDisplay.Should().BeNull();
    }

    [Fact]
    public async Task DecodeFailure_IsReported_WithoutPublishingHalfAComparison()
    {
        var frames = new FrameService((path, _) => path == "left" ? Task.FromResult<byte[]>([1])
            : Task.FromException<byte[]>(new InvalidOperationException("decoder failed")));
        using var vm = new ComparisonPlaybackViewModel(null, frames);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 2), 12, 24);
        await vm.RefreshFramesAsync();
        vm.FramesToDisplay.Should().BeNull();
        vm.Status.Should().Contain("decoder failed");
    }

    private sealed class FrameService(Func<string, CancellationToken, Task<byte[]>> decode) : IComparisonFrameService
    {
        public Task<byte[]> DecodeAsync(string path, double seconds, CropRect? crop, CancellationToken ct = default) => decode(path, ct);
    }

    private sealed class CachedFrameService : IComparisonFrameService
    {
        public int Prefetches { get; private set; }
        public Task<byte[]> DecodeAsync(string path, double seconds, CropRect? crop, CancellationToken ct = default) =>
            throw new InvalidOperationException("Cached frame steps must not decode again.");
        public bool TryGetFrame(string path, double seconds, CropRect? crop, out byte[] frame)
        {
            frame = path == "left" ? [1] : [2];
            return true;
        }
        public void Prefetch(string path, double seconds, double frameDuration, int count, CropRect? crop) => Prefetches++;
    }
}
