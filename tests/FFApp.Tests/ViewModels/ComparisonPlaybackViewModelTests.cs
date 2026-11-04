using System.Buffers;
using System.Collections.Concurrent;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class ComparisonPlaybackViewModelTests
{
    [Fact]
    public async Task ResetAsync_WaitsForDecoderRelease_AndAllowsPlaybackToBeReopened()
    {
        var factory = new DecoderFactory(25);
        await using var vm = new ComparisonPlaybackViewModel(factory);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), 12, 25);
        await vm.RenderAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        factory.Items["right"].BeforeRead = () => { entered.Set(); release.Wait(); };
        vm.PositionSeconds = 1;
        var pending = vm.RenderAsync();
        try
        {
            entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
            var reset = vm.ResetAsync();
            reset.IsCompleted.Should().BeFalse();
            release.Set();
            await Task.WhenAll(pending, reset);
            factory.Items.Values.Should().OnlyContain(x => x.Disposed);
            vm.HasMedia.Should().BeFalse();
            vm.FramesToDisplay.Should().BeNull();
            vm.LoadSource(new ComparisonTrack("source", 0), 12, 25);
            await vm.RenderAsync();
            vm.HasMedia.Should().BeTrue();
            factory.Items["source"].Disposed.Should().BeFalse();
        }
        finally { release.Set(); }
    }

    [Theory]
    [InlineData(24000.0 / 1001)]
    [InlineData(60)]
    public async Task Stepping_ReusesBothDecoders_AndPublishesMatchingFrames(double fps)
    {
        var factory = new DecoderFactory(fps);
        await using var vm = new ComparisonPlaybackViewModel(factory);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), 12, fps);
        await vm.RenderAsync();
        vm.HasMedia.Should().BeTrue();
        vm.IsAvailable.Should().BeTrue();
        for (var frame = 1; frame <= 20; frame++)
        {
            await vm.StepForwardCommand.ExecuteAsync(null);
            vm.PositionSeconds.Should().BeApproximately(frame / fps, 1e-7);
            vm.FramesToDisplay?.Left.Seconds.Should().BeApproximately(frame / fps, 1e-7);
            vm.FramesToDisplay?.Right?.Seconds.Should().BeApproximately(frame / fps, 1e-7);
        }
        factory.OpenCount.Should().Be(2);
        factory.Items["left"].NextCount.Should().Be(20);
        factory.Items["right"].NextCount.Should().Be(0);
    }

    [Fact]
    public async Task SourceAndEncodedPreRoll_MapToSameSourceFrame()
    {
        const double fps = 24000.0 / 1001;
        var factory = new DecoderFactory(fps);
        await using var vm = new ComparisonPlaybackViewModel(factory);
        const double sampleStart = 1684.656;
        const double clipStart = sampleStart - 2;
        vm.Load(new ComparisonTrack("left", sampleStart),
            new ComparisonTrack("right", 2, SourceStartSeconds: clipStart), 12, fps);
        await vm.RenderAsync();
        var origin = Math.Ceiling(clipStart * fps - 1e-7) / fps;
        vm.FramesToDisplay.Should().NotBeNull();
        var first = vm.FramesToDisplay ?? throw new InvalidOperationException("Missing pair.");
        var firstRight = first.Right ?? throw new InvalidOperationException("Missing right frame.");
        first.Left.Seconds.Should().BeApproximately(firstRight.Seconds + origin, 1e-6);
        await vm.StepForwardCommand.ExecuteAsync(null);
        var second = vm.FramesToDisplay ?? throw new InvalidOperationException("Missing pair.");
        var secondRight = second.Right ?? throw new InvalidOperationException("Missing right frame.");
        second.Left.Seconds.Should().BeApproximately(secondRight.Seconds + origin, 1e-6);
    }

    [Fact]
    public async Task VariableFrameTimes_StepToActualPresentationTimestamp()
    {
        var factory = new DecoderFactory(25, [0, .04, .1, .18]);
        await using var vm = new ComparisonPlaybackViewModel(factory);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), .3, 25);
        await vm.RenderAsync();
        foreach (var expected in new[] { .04, .1, .18 })
        {
            await vm.StepForwardCommand.ExecuteAsync(null);
            vm.PositionSeconds.Should().BeApproximately(expected, 1e-9);
            vm.FramesToDisplay?.Right?.Seconds.Should().BeApproximately(expected, 1e-9);
        }
        await vm.StepForwardCommand.ExecuteAsync(null);
        vm.PositionSeconds.Should().Be(.18);
    }

    [Fact]
    public async Task Playback_UsesOneClock_StopsAtEnd_AndPauseRetainsDisplayedPair()
    {
        var factory = new DecoderFactory(25, [0, .04, .1, .18]);
        var time = new PlaybackTimeProvider();
        await using var vm = new ComparisonPlaybackViewModel(factory, time);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), .15, 25);
        await vm.RenderAsync();
        vm.Loop = false;
        var presented = new List<double>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.FramesToDisplay) && vm.FramesToDisplay is { } pair)
            {
                pair.Left.Seconds.Should().Be(pair.Right?.Seconds);
                presented.Add(pair.Left.Seconds);
            }
        };
        await vm.PlayAsync();
        presented.Should().Equal(.04, .1);
        time.Delays.Should().HaveCount(2);
        time.Delays.Sum(x => x.TotalSeconds).Should().BeApproximately(.1, 1e-6);
        vm.IsPlaying.Should().BeFalse();
        var last = vm.FramesToDisplay;
        vm.Pause();
        vm.FramesToDisplay.Should().BeSameAs(last);
        factory.OpenCount.Should().Be(2);
    }

    [Fact]
    public async Task Playback_DecodesNextFrame_WhileCurrentFrameWaitsForItsTime()
    {
        var factory = new DecoderFactory(25, [0, .04, .1, .18]);
        var time = new PlaybackTimeProvider();
        await using var vm = new ComparisonPlaybackViewModel(factory, time);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), .15, 25);
        await vm.RenderAsync();
        vm.Loop = false;
        var left = factory.Items["left"];
        var readsBeforeDelay = new List<bool>();
        // Without read-ahead the next read only starts after the delay, so the wait would time out.
        time.BeforeDelay = () => readsBeforeDelay.Add(
            SpinWait.SpinUntil(() => left.NextCount >= readsBeforeDelay.Count + 2, TimeSpan.FromSeconds(5)));
        await vm.PlayAsync();
        readsBeforeDelay.Should().Equal(true, true);
        vm.FramesToDisplay?.Left.Seconds.Should().Be(.1);
    }

    [Theory]
    [InlineData(-0.05, true, true)]
    [InlineData(-0.05, false, false)]
    [InlineData(-0.03, true, false)]
    [InlineData(0.01, true, false)]
    public void ShouldSkip_OnlyDropsFramesMoreThanAFrameLate_WhenANewerOneIsReady(
        double remainingSeconds, bool newerReady, bool expected) =>
        ComparisonPlaybackViewModel.ShouldSkip(TimeSpan.FromSeconds(remainingSeconds), .04, newerReady)
            .Should().Be(expected);

    [Fact]
    public async Task Loop_RestartsBothDecodersTogether_AndCanBePaused()
    {
        var factory = new DecoderFactory(25, [0, .04, .08, .12]);
        await using var vm = new ComparisonPlaybackViewModel(factory, new PlaybackTimeProvider());
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), .1, 25);
        await vm.RenderAsync();
        var presented = new List<double>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(vm.FramesToDisplay) || vm.FramesToDisplay is not { } pair) return;
            presented.Add(pair.Left.Seconds);
            pair.Right?.Seconds.Should().Be(pair.Left.Seconds);
            if (presented.Count == 4) vm.Pause();
        };
        await vm.PlayAsync();
        presented.Should().Equal(.04, .08, 0, .04);
        vm.IsPlaying.Should().BeFalse();
    }

    [Fact]
    public async Task LatestSeekWins_AndNeverPublishesHalfAPair()
    {
        var factory = new DecoderFactory(25);
        await using var vm = new ComparisonPlaybackViewModel(factory);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), 12, 25);
        await vm.RenderAsync();
        var oldPair = vm.FramesToDisplay;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        factory.Items["right"].BeforeRead = () => { entered.Set(); release.Wait(); };
        vm.PositionSeconds = 1;
        var stale = vm.RenderAsync();
        entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        vm.FramesToDisplay.Should().BeSameAs(oldPair);
        vm.PositionSeconds = 2;
        var latest = vm.RenderAsync();
        factory.Items["right"].BeforeRead = null;
        release.Set();
        await Task.WhenAll(stale, latest);
        vm.PositionSeconds.Should().Be(2);
        vm.FramesToDisplay?.Left.Seconds.Should().Be(2);
        vm.FramesToDisplay?.Right?.Seconds.Should().Be(2);
    }

    [Fact]
    public async Task Dispose_CancelsWork_WaitsForNativeCall_ThenClosesBothDecoders()
    {
        var factory = new DecoderFactory(25);
        var vm = new ComparisonPlaybackViewModel(factory);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), 12, 25);
        await vm.RenderAsync();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        factory.Items["right"].BeforeRead = () => { entered.Set(); release.Wait(); };
        vm.PositionSeconds = 1;
        var pending = vm.RenderAsync();
        entered.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();
        var closing = vm.DisposeAsync().AsTask();
        closing.IsCompleted.Should().BeFalse();
        factory.Items.Values.Should().OnlyContain(x => !x.Disposed);
        release.Set();
        await Task.WhenAll(pending, closing);
        factory.Items.Values.Should().OnlyContain(x => x.Disposed);
        vm.FramesToDisplay.Should().BeNull();
    }

    [Fact]
    public async Task DecodeFailure_RetainsLastCompletePair_AndReportsError()
    {
        var factory = new DecoderFactory(25);
        await using var vm = new ComparisonPlaybackViewModel(factory);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), 12, 25);
        await vm.RenderAsync();
        var previous = vm.FramesToDisplay;
        factory.Items["right"].BeforeRead = () => throw new InvalidDataException("bad frame");
        await vm.RenderAsync(next: true);
        vm.FramesToDisplay.Should().BeSameAs(previous);
        vm.Status.Should().Contain("bad frame");
    }

    [Fact]
    public async Task SwitchingSamples_ClosesOldDecoders_AndClearsOldFrames()
    {
        var factory = new DecoderFactory(25);
        await using var vm = new ComparisonPlaybackViewModel(factory);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), 12, 25);
        await vm.RenderAsync();
        var old = factory.Items.Values.ToArray();
        vm.LoadSource(new ComparisonTrack("new", 0), 5, 25);
        vm.FramesToDisplay.Should().BeNull();
        await vm.RenderAsync();
        old.Should().OnlyContain(x => x.Disposed);
        factory.OpenCount.Should().Be(3);
        vm.IsSourcePreview.Should().BeTrue();
        vm.FramesToDisplay?.Right.Should().BeNull();
    }

    [Theory]
    [InlineData(0.5, 0)]
    [InlineData(5.25, 4.25)]
    public void SeekBackward_PausesAndClamps(double position, double expected)
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.LoadSource(new ComparisonTrack("source", 100), 12, 25);
        vm.PositionSeconds = position;
        vm.IsPlaying = true;
        vm.SeekBackwardCommand.Execute(null);
        vm.IsPlaying.Should().BeFalse();
        vm.PositionSeconds.Should().Be(expected);
    }

    [Theory]
    [InlineData(5, 6)]
    [InlineData(11.5, 11.96)]
    public void SeekForward_PausesAndStopsAtLastFrame(double position, double expected)
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.LoadSource(new ComparisonTrack("source", 100), 12, 25);
        vm.PositionSeconds = position;
        vm.IsPlaying = true;
        vm.SeekForwardCommand.Execute(null);
        vm.IsPlaying.Should().BeFalse();
        vm.PositionSeconds.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(1, 0.96)]
    [InlineData(0.01, 0)]
    public void StepBackward_MovesOneFrameBack_AndClampsAtStart(double position, double expected)
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.LoadSource(new ComparisonTrack("source", 100), 12, 25);
        vm.HasMedia = true;
        vm.PositionSeconds = position;
        vm.IsPlaying = true;
        vm.StepBackwardCommand.Execute(null);
        vm.IsPlaying.Should().BeFalse();
        vm.PositionSeconds.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(7.5, 7.5)]
    [InlineData(-1, 0)]
    [InlineData(30, 11.96)]
    [InlineData(double.NaN, 0)]
    public void Load_StartsAtRequestedPosition_ClampedToSample(double position, double expected)
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.Load(new ComparisonTrack("left", 2), new ComparisonTrack("right", 2), 12, 25, position);
        vm.PositionSeconds.Should().BeApproximately(expected, 1e-9);
        vm.LoadSource(new ComparisonTrack("source", 100), 12, 25);
        vm.PositionSeconds.Should().Be(0);
    }

    [Fact]
    public void StepBackward_WithoutMedia_DoesNothing()
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.LoadSource(new ComparisonTrack("source", 100), 12, 25);
        vm.PositionSeconds = 1;
        vm.StepBackwardCommand.Execute(null);
        vm.PositionSeconds.Should().Be(1);
    }

    [Fact]
    public void PositionAndDuration_UseCompactSampleTimecode()
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.LoadSource(new ComparisonTrack("source", 100), 12, 25);
        using var changes = vm.Monitor();
        vm.PositionSeconds = 3.36;
        changes.Should().RaisePropertyChangeFor(v => v.PositionDisplay);
        vm.PositionDisplay.Should().Be("00:03.360");
        vm.DurationDisplay.Should().Be("/ 00:12.000");
    }

    [Fact]
    public void SliderLayout_IsOfferedForComparisons_ButNotForSourcePreview()
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.LayoutOptions.Should().Contain(ComparisonPlaybackViewModel.SliderLayout);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), 12, 25);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.Layout = ComparisonPlaybackViewModel.SliderLayout;
        vm.IsSliderLayout.Should().BeTrue();
        changed.Should().Contain(nameof(ComparisonPlaybackViewModel.IsSliderLayout));
        vm.LoadSource(new ComparisonTrack("source", 0), 12, 25);
        vm.IsSliderLayout.Should().BeFalse();
        vm.Layout.Should().Be(ComparisonPlaybackViewModel.AOnlyLayout);
    }

    [Theory]
    [InlineData(0.25, 0.25)]
    [InlineData(-0.5, 0)]
    [InlineData(1.5, 1)]
    [InlineData(double.NaN, 0.5)]
    public void SplitPosition_IsClampedToPaneWidth(double value, double expected)
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.SplitPosition.Should().Be(0.5);
        vm.SplitPosition = value;
        vm.SplitPosition.Should().Be(expected);
    }

    [Fact]
    public void MoveSplit_StopsAtEdges_AndResetSplitReturnsToCenter()
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.MoveSplit(0.1);
        vm.SplitPosition.Should().BeApproximately(0.6, 1e-9);
        vm.MoveSplit(-2);
        vm.SplitPosition.Should().Be(0);
        vm.ResetSplit();
        vm.SplitPosition.Should().Be(0.5);
    }

    private sealed class DecoderFactory(double fps, double[]? timestamps = null) : IComparisonDecoderFactory
    {
        public ConcurrentDictionary<string, Decoder> Items { get; } = new();
        public int OpenCount;
        public IComparisonDecoder Open(string path, CropRect? crop, CancellationToken ct = default)
        {
            Interlocked.Increment(ref OpenCount);
            var decoder = new Decoder(fps, timestamps);
            Items[path] = decoder;
            return decoder;
        }
    }
    private sealed class Decoder(double fps, double[]? timestamps) : IComparisonDecoder
    {
        public int NextCount { get; private set; }
        public bool Disposed { get; private set; }
        public Action? BeforeRead { get; set; }
        private double _seconds;
        public ComparisonVideoFrame ReadAt(double seconds, CancellationToken ct = default)
        {
            BeforeRead?.Invoke();
            ct.ThrowIfCancellationRequested();
            _seconds = timestamps?.FirstOrDefault(x => x >= seconds - 1e-7, timestamps[^1])
                ?? Math.Ceiling(seconds * fps - 1e-7) / fps;
            return Frame(_seconds);
        }
        public ComparisonVideoFrame? ReadNext(double? afterSeconds = null, CancellationToken ct = default)
        {
            if (afterSeconds is { } after) { using var current = ReadAt(after, ct); }
            ct.ThrowIfCancellationRequested();
            NextCount++;
            _seconds = timestamps?.FirstOrDefault(x => x > _seconds + 1e-7, double.PositiveInfinity) ?? _seconds + 1 / fps;
            return double.IsPositiveInfinity(_seconds) ? null : Frame(_seconds);
        }
        public void Dispose() => Disposed = true;
    }
    internal static ComparisonVideoFrame Frame(double seconds)
    {
        var pixels = MemoryPool<byte>.Shared.Rent(16);
        pixels.Memory.Span[..16].Fill(255);
        return new ComparisonVideoFrame(2, 2, seconds, pixels);
    }
    private sealed class PlaybackTimeProvider : TimeProvider
    {
        private long _timestamp;
        public List<TimeSpan> Delays { get; } = [];
        public Action? BeforeDelay { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            BeforeDelay?.Invoke();
            Delays.Add(dueTime);
            Interlocked.Add(ref _timestamp, dueTime.Ticks);
            return new Timer(callback, state, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
}
