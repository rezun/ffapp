using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFApp.ViewModels;
using FFmpeg.AutoGen;
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

/// <summary>HDR tests additionally need libavfilter with zscale (zimg) and tonemap.</summary>
public sealed class NativeToneMapFactAttribute : FactAttribute
{
    public NativeToneMapFactAttribute()
    {
        Skip = new NativeComparisonFactAttribute().Skip;
        if (Skip is not null) return;
        var factory = new ComparisonDecoderFactory(new ExecutablePathsConfig());
        using (factory.Open(Path.Combine(AppContext.BaseDirectory, "Fixtures", "comparison.mp4"), null)) { }
        if (!factory.IsToneMappingAvailable) Skip = "FFmpeg is missing libavfilter, zscale or tonemap.";
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
    public async Task FractionalFrameRate_MixedTimeBases_AdvanceEveryFrame_InBothOrders()
    {
        const double fps = 24000.0 / 1001;
        const double sampleStart = 1.316;
        const double trimStart = 1.116;
        var source = new ComparisonTrack(fixture.Video("comparison-23976.mp4"), sampleStart);
        var clip = new ComparisonTrack(fixture.Video("comparison-23976-trim.webm"), .2, SourceStartSeconds: trimStart);
        var clipOrigin = Math.Ceiling(trimStart * fps) / fps;
        foreach (var tracks in new[] { (source, clip), (clip, source), (source, source), (clip, clip) })
        {
            await using var vm = new ComparisonPlaybackViewModel(fixture.Factory);
            vm.Load(tracks.Item1, tracks.Item2, 1.2, fps);
            await vm.RenderAsync();
            for (var index = 0; index <= 20; index++)
            {
                if (index > 0) await vm.StepForwardCommand.ExecuteAsync(null);
                var frames = vm.FramesToDisplay ?? throw new InvalidOperationException(vm.Status);
                var right = frames.Right ?? throw new InvalidOperationException("Missing right frame.");
                var leftOrigin = tracks.Item1 == source ? 0 : clipOrigin;
                var rightOrigin = tracks.Item2 == source ? 0 : clipOrigin;
                var expectedFrame = Math.Ceiling(sampleStart * fps) + index;
                Math.Round((frames.Left.Seconds + leftOrigin) * fps).Should().Be(expectedFrame,
                    "the left side must advance exactly one source frame per step");
                Math.Round((right.Seconds + rightOrigin) * fps).Should().Be(expectedFrame,
                    "timestamp quantization must not skip or repeat frames on the right");
                frames.Left.Pixels.ToArray().Should().Equal(right.Pixels.ToArray());
            }
        }
    }

    [NativeComparisonFact]
    public async Task FractionalFrameRate_PlaybackHasNoRepeatedOrSkippedFrames_WhenTracksAreSwapped()
    {
        const double fps = 24000.0 / 1001;
        var source = new ComparisonTrack(fixture.Video("comparison-23976.mp4"), 1.316);
        var clip = new ComparisonTrack(fixture.Video("comparison-23976-trim.webm"), .2, SourceStartSeconds: 1.116);
        var clipOrigin = Math.Ceiling(1.116 * fps) / fps;
        foreach (var tracks in new[] { (source, clip), (clip, source) })
        {
            await using var vm = new ComparisonPlaybackViewModel(fixture.Factory);
            vm.Load(tracks.Item1, tracks.Item2, .5, fps);
            await vm.RenderAsync();
            vm.Loop = false;
            var leftIndices = new List<double>();
            var rightIndices = new List<double>();
            var matchingPixels = new List<bool>();
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(vm.FramesToDisplay) || vm.FramesToDisplay is not { Right: { } right } frames) return;
                leftIndices.Add(Math.Round((frames.Left.Seconds + (tracks.Item1 == source ? 0 : clipOrigin)) * fps));
                rightIndices.Add(Math.Round((right.Seconds + (tracks.Item2 == source ? 0 : clipOrigin)) * fps));
                matchingPixels.Add(frames.Left.Pixels.Span.SequenceEqual(right.Pixels.Span));
            };
            await vm.PlayAsync().WaitAsync(TimeSpan.FromSeconds(5));
            vm.Status.Should().NotContain("failed");
            leftIndices.Count.Should().BeGreaterThan(8);
            leftIndices.Should().Equal(Enumerable.Range(1, leftIndices.Count).Select(index => Math.Ceiling(1.316 * fps) + index));
            rightIndices.Should().Equal(leftIndices);
            matchingPixels.Should().OnlyContain(matches => matches);
        }
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

    [Theory]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, 1920, 1080, AVColorSpace.AVCOL_SPC_BT709)]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, 1920, 800, AVColorSpace.AVCOL_SPC_BT709)]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, 1280, 536, AVColorSpace.AVCOL_SPC_BT709)]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, 1024, 720, AVColorSpace.AVCOL_SPC_BT709)]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, 720, 576, AVColorSpace.AVCOL_SPC_SMPTE170M)]
    [InlineData(AVColorSpace.AVCOL_SPC_UNSPECIFIED, 720, 480, AVColorSpace.AVCOL_SPC_SMPTE170M)]
    [InlineData(AVColorSpace.AVCOL_SPC_BT470BG, 1920, 1080, AVColorSpace.AVCOL_SPC_BT470BG)]
    [InlineData(AVColorSpace.AVCOL_SPC_BT709, 720, 576, AVColorSpace.AVCOL_SPC_BT709)]
    [InlineData(AVColorSpace.AVCOL_SPC_BT2020_NCL, 3840, 2160, AVColorSpace.AVCOL_SPC_BT2020_NCL)]
    public void EffectiveColorSpace_GuessesOnlyUntaggedFrames_ByResolution(
        AVColorSpace tagged, int width, int height, AVColorSpace expected) =>
        NativeComparisonDecoder.EffectiveColorSpace(tagged, width, height).Should().Be(expected);

    // comparison-untagged-hd.mp4: 1280x720 H.264 without color tags, converted from RGB with the BT.709 matrix;
    // left half #C03020, right half #2A7A80. Decoding with BT.601 would give red (177,34,32).
    [NativeComparisonFact]
    public void UntaggedHdFrames_AreDecodedWithBt709()
    {
        using var decoder = fixture.Factory.Open(fixture.Video("comparison-untagged-hd.mp4"), null);
        using var frame = decoder.ReadAt(0);
        var red = Pixel(frame, 100, 300);
        ((int)red.R).Should().BeCloseTo(189, 4);
        ((int)red.G).Should().BeCloseTo(47, 4);
        ((int)red.B).Should().BeCloseTo(30, 4);
        var teal = Pixel(frame, 1000, 300);
        ((int)teal.R).Should().BeCloseTo(40, 4);
        ((int)teal.G).Should().BeCloseTo(121, 4);
        ((int)teal.B).Should().BeCloseTo(126, 4);
    }

    // comparison-hdr10.mp4: 64x48 HDR10 (PQ, BT.2020), left half BT.709 red, right half 50% gray.
    private static (byte B, byte G, byte R) Pixel(ComparisonVideoFrame frame, int x, int y)
    {
        var offset = (y * frame.Width + x) * 4;
        var pixels = frame.Pixels.Span;
        return (pixels[offset], pixels[offset + 1], pixels[offset + 2]);
    }

    [NativeToneMapFact]
    public void HdrFrames_AreToneMappedToBt709()
    {
        using var decoder = fixture.Factory.Open(fixture.Video("comparison-hdr10.mp4"), null);
        using var frame = decoder.ReadAt(0);
        frame.Width.Should().Be(64);
        frame.Height.Should().Be(48);
        var red = Pixel(frame, 8, 24);
        red.R.Should().BeGreaterThan(120);
        red.G.Should().BeLessThan(20);
        red.B.Should().BeLessThan(20);
        var gray = Pixel(frame, 48, 24);
        gray.R.Should().BeInRange(60, 160);
        ((int)gray.G).Should().BeCloseTo(gray.R, 2);
        ((int)gray.B).Should().BeCloseTo(gray.R, 2);
    }

    [NativeToneMapFact]
    public void Factory_UsesVideoToolboxOnMacOS_AndCpuClutElsewhere()
    {
        using var decoder = fixture.Factory.Open(fixture.Video(), null);
        fixture.Factory.HdrConversion.Should().Be(OperatingSystem.IsMacOS() ? HdrConversion.VideoToolbox : HdrConversion.Clut);
    }

    [NativeToneMapFact]
    public void HdrFrames_ClutFallback_AlsoMapsToBt709()
    {
        using var decoder = new NativeComparisonDecoder(fixture.Video("comparison-hdr10.mp4"), null, HdrConversion.Clut, default);
        using var frame = decoder.ReadAt(0);
        var red = Pixel(frame, 8, 24);
        red.R.Should().BeGreaterThan(120);
        red.G.Should().BeLessThan(20);
        red.B.Should().BeLessThan(20);
    }

    [NativeToneMapFact]
    public void HdrFrames_WithoutToneMapping_ShowDesaturatedBt2020Values()
    {
        // Guards the fixture: without the HDR path, red is visibly wrong, so the test above is meaningful.
        using var decoder = new NativeComparisonDecoder(fixture.Video("comparison-hdr10.mp4"), null, HdrConversion.None, default);
        using var frame = decoder.ReadAt(0);
        var red = Pixel(frame, 8, 24);
        red.G.Should().BeGreaterThan(40);
        red.B.Should().BeGreaterThan(40);
    }

    [NativeToneMapFact]
    public void HdrFrames_SeekAndStepReturnIdenticalPixels_AndCropMatchesFullFrame()
    {
        using var decoder = fixture.Factory.Open(fixture.Video("comparison-hdr10.mp4"), null);
        using var first = decoder.ReadAt(.08);
        using var again = decoder.ReadAt(.08);
        again.Pixels.ToArray().Should().Equal(first.Pixels.ToArray());
        using var next = decoder.ReadNext();
        next?.Seconds.Should().BeApproximately(.12, 1e-7);
        using var back = decoder.ReadAt(.08);
        back.Pixels.ToArray().Should().Equal(first.Pixels.ToArray());

        using var cropDecoder = fixture.Factory.Open(fixture.Video("comparison-hdr10.mp4"), new CropRect(3, 5, 17, 13));
        using var cropped = cropDecoder.ReadAt(.08);
        for (var row = 0; row < cropped.Height; row++)
            cropped.Pixels.Span.Slice(row * 17 * 4, 17 * 4).ToArray().Should()
                .Equal(first.Pixels.Span.Slice(((row + 5) * 64 + 3) * 4, 17 * 4).ToArray());
    }

    [NativeToneMapFact]
    public void SdrFrames_AreNotChangedByToneMapping()
    {
        using var toneMapped = fixture.Factory.Open(fixture.Video(), null);
        using var plain = new NativeComparisonDecoder(fixture.Video(), null, HdrConversion.None, default);
        using var expected = plain.ReadAt(.4);
        using var actual = toneMapped.ReadAt(.4);
        actual.Pixels.ToArray().Should().Equal(expected.Pixels.ToArray());
    }
}
