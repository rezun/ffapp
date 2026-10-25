using FFApp.ViewModels;
using FluentAssertions;

namespace FFApp.Tests.ViewModels;

public sealed class ComparisonPlaybackViewModelTests
{
    [Theory]
    [InlineData(24, 5.25, 4.25)]
    [InlineData(60, 5.25, 4.25)]
    [InlineData(24000.0 / 1001, 5.25, 4.25)]
    [InlineData(24, 0.5, 0)]
    [InlineData(60, 0, 0)]
    public void SeekBackward_SeeksOneSecond_Pauses_AndStopsAtSampleStart(
        double frameRate, double position, double expected)
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.Load(new ComparisonTrack("original", 1684.656), new ComparisonTrack("encoded", 2), 12, frameRate);
        vm.PositionSeconds = position;
        vm.IsPlaying = true;

        vm.SeekBackwardCommand.Execute(null);

        vm.PositionSeconds.Should().Be(expected);
        vm.IsPlaying.Should().BeFalse();
    }

    [Theory]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(24000.0 / 1001)]
    [InlineData(30000.0 / 1001)]
    public void AdvanceFrame_AdvancesEachDecoderOncePerClick_UsingSourceFrameRate(double frameRate)
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.Load(new ComparisonTrack("original", 1684.656), new ComparisonTrack("encoded", 2), 12, frameRate);
        vm.IsPlaying = true;
        var leftFrames = 0;
        var rightFrames = 0;

        for (var click = 1; click <= 20; click++)
        {
            vm.AdvanceFrame(() => leftFrames++, () => rightFrames++);
            leftFrames.Should().Be(click);
            rightFrames.Should().Be(click);
            vm.PositionSeconds.Should().BeApproximately(click / frameRate, 1e-10);
            vm.IsPlaying.Should().BeFalse();
        }
    }

    [Fact]
    public void AdvanceFrame_SinglePreview_DoesNotRequireSecondPlayer()
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.LoadSource(new ComparisonTrack("original", 100), 12, 25);
        vm.PositionSeconds = 2.5;
        var frames = 0;
        vm.AdvanceFrame(() => frames++, null);
        frames.Should().Be(1);
        vm.PositionSeconds.Should().BeApproximately(2.54, 1e-10);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(60)]
    public void AdvanceFrame_StopsAtLastFrame_WithoutAdvancingDecoderAgain(double frameRate)
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.LoadSource(new ComparisonTrack("original", 0), 1, frameRate);
        vm.PositionSeconds = 1 - 1 / frameRate;
        var frames = 0;
        vm.AdvanceFrame(() => frames++, null);
        frames.Should().Be(0);
        vm.PositionSeconds.Should().Be(1 - 1 / frameRate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AdvanceFrame_InvalidFrameRate_UsesFallback(double? frameRate)
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.LoadSource(new ComparisonTrack("original", 0), 12, frameRate);
        vm.AdvanceFrame(() => { }, null);
        vm.PositionSeconds.Should().Be(1.0 / 24);
    }

    [Fact]
    public void StepForward_WithoutLoadedPlayer_DoesNotInventPositionChange()
    {
        using var vm = new ComparisonPlaybackViewModel(null);
        vm.StepForwardCommand.Execute(null);
        vm.PositionSeconds.Should().Be(0);
    }
}
