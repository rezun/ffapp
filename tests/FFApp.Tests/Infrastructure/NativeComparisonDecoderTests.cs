using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Infrastructure;
using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

[CollectionDefinition("Native comparison")]
public sealed class NativeComparisonCollection : ICollectionFixture<NativeComparisonFixture>;

public sealed class NativeComparisonFixture
{
    public ComparisonDecoderFactory Factory { get; } = new(new ExecutablePathsConfig());
    public string Video(string name = "comparison.mp4") => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
}

/// <summary>Native regression tests run when the optional FFmpeg 9 shared runtime is installed.</summary>
public sealed class NativeComparisonFactAttribute : FactAttribute
{
    public NativeComparisonFactAttribute()
    {
        var directory = ComparisonDecoderFactory.FindLibraryDirectory("ffmpeg");
        var name = OperatingSystem.IsMacOS() ? "libavcodec.63.dylib"
            : OperatingSystem.IsWindows() ? "avcodec-63.dll" : "libavcodec.so.63";
        if (!File.Exists(Path.Combine(directory, name))) Skip = "FFmpeg 9 shared libraries are not installed.";
    }
}

[Collection("Native comparison")]
public sealed class NativeComparisonDecoderTests(NativeComparisonFixture fixture)
{
    [NativeComparisonFact]
    public void SeekForwardStepAndBackwardSeek_ReturnIdenticalPixelsAtIdenticalTimestamps()
    {
        using var decoder = fixture.Factory.Open(fixture.Video(), null);
        using var first = decoder.ReadAt(.32);
        first.Seconds.Should().BeApproximately(.32, 1e-7);
        first.Width.Should().Be(64);
        first.Height.Should().Be(48);
        using var next = decoder.ReadNext();
        next.Should().NotBeNull();
        next?.Seconds.Should().BeApproximately(.36, 1e-7);
        using var back = decoder.ReadAt(.12);
        back.Seconds.Should().BeApproximately(.12, 1e-7);
        using var again = decoder.ReadAt(.32);
        again.Pixels.ToArray().Should().Equal(first.Pixels.ToArray());
        using var repeatedNext = decoder.ReadNext();
        repeatedNext?.Pixels.ToArray().Should().Equal(next?.Pixels.ToArray());
    }

    [NativeComparisonFact]
    public void BFrames_AreDrainedAtEnd_AndDecoderCanSeekAgain()
    {
        using var decoder = fixture.Factory.Open(fixture.Video(), null);
        for (var index = 0; index < 25; index++)
        {
            using var frame = decoder.ReadNext();
            frame.Should().NotBeNull();
            frame?.Seconds.Should().BeApproximately(index / 25.0, 1e-7);
        }
        decoder.ReadNext().Should().BeNull();
        using var last = decoder.ReadAt(.999);
        last.Seconds.Should().BeApproximately(.96, 1e-7);
        last.Pixels.Length.Should().Be(64 * 48 * 4);
        using var start = decoder.ReadAt(0);
        start.Seconds.Should().Be(0);
    }

    [NativeComparisonFact]
    public void VfrFrames_StepUsingActualPresentationTimestamps()
    {
        using var decoder = fixture.Factory.Open(fixture.Video("comparison-vfr.mp4"), null);
        using var start = decoder.ReadAt(0);
        using var next = decoder.ReadNext();
        next?.Seconds.Should().BeApproximately(.12, 1e-7);
        using var sought = decoder.ReadAt(.13);
        sought.Seconds.Should().BeApproximately(.24, 1e-7);
        using var stepped = decoder.ReadNext();
        stepped?.Seconds.Should().BeApproximately(.36, 1e-7);
    }

    [NativeComparisonFact]
    public void Crop_PreservesExactSourcePixels_IncludingUnalignedCoordinates()
    {
        using var fullDecoder = fixture.Factory.Open(fixture.Video(), null);
        using var cropDecoder = fixture.Factory.Open(fixture.Video(), new CropRect(3, 5, 17, 13));
        using var full = fullDecoder.ReadAt(.4);
        using var cropped = cropDecoder.ReadAt(.4);
        cropped.Width.Should().Be(17);
        cropped.Height.Should().Be(13);
        for (var row = 0; row < cropped.Height; row++)
            cropped.Pixels.Span.Slice(row * 17 * 4, 17 * 4).ToArray().Should()
                .Equal(full.Pixels.Span.Slice(((row + 5) * 64 + 3) * 4, 17 * 4).ToArray());
    }

    [NativeComparisonFact]
    public void Cancellation_DoesNotDestroyDecoder_AndDisposedDecoderRejectsReads()
    {
        var decoder = fixture.Factory.Open(fixture.Video(), null);
        using var frame = decoder.ReadAt(.24);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = () => decoder.ReadNext(ct: cancellation.Token);
        cancelled.Should().Throw<OperationCanceledException>();
        using var next = decoder.ReadNext(.24);
        next?.Seconds.Should().BeApproximately(.28, 1e-7);
        decoder.Dispose();
        decoder.Dispose();
        var disposed = () => decoder.ReadAt(0);
        disposed.Should().Throw<ObjectDisposedException>();
    }

    [NativeComparisonFact]
    public async Task SourceAndLosslessTrim_StayPixelIdentical_WhenSeekingAndStepping()
    {
        await using var vm = new ComparisonPlaybackViewModel(fixture.Factory);
        vm.Load(new ComparisonTrack(fixture.Video(), .316),
            new ComparisonTrack(fixture.Video("comparison-trim.mp4"), .2, SourceStartSeconds: .116), .5, 25);
        await vm.RenderAsync();
        AssertPair(vm);
        for (var index = 0; index < 8; index++)
        {
            await vm.StepForwardCommand.ExecuteAsync(null);
            AssertPair(vm);
        }
        vm.PositionSeconds = .08;
        await vm.RenderAsync();
        AssertPair(vm);
        vm.PositionSeconds.Should().BeApproximately(.08, 1e-7);
        await vm.StepForwardCommand.ExecuteAsync(null);
        AssertPair(vm);
    }

    private static void AssertPair(ComparisonPlaybackViewModel vm)
    {
        vm.Status.Should().NotContain("failed");
        var pair = vm.FramesToDisplay ?? throw new InvalidOperationException("Missing frame pair.");
        var right = pair.Right ?? throw new InvalidOperationException("Missing right frame.");
        pair.Left.Seconds.Should().BeApproximately(right.Seconds + .12, 1e-7);
        pair.Left.Pixels.ToArray().Should().Equal(right.Pixels.ToArray());
    }

    [NativeComparisonFact]
    public void InvalidFileAndCrop_ReportErrors_WithoutLeakingOrCrashing()
    {
        var missing = () => fixture.Factory.Open(fixture.Video("missing.mp4"), null);
        missing.Should().Throw<InvalidDataException>();
        using var decoder = fixture.Factory.Open(fixture.Video(), new CropRect(60, 0, 10, 48));
        var invalidCrop = () => decoder.ReadAt(0);
        invalidCrop.Should().Throw<InvalidDataException>().WithMessage("*crop*");
    }
}
