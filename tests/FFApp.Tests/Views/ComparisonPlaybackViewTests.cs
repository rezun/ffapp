using System.Buffers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using FFApp.Core.Services;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class ComparisonPlaybackViewTests(QueueTestSession fixture)
{
    [Fact]
    public async Task SliderLayout_StacksPanes_AndClipsBToTheRightOfTheDivider()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var vm = Comparison();
            var (window, view) = Show(vm);
            try
            {
                vm.Layout = ComparisonPlaybackViewModel.SliderLayout;
                window.UpdateLayout();
                var left = Find<Border>(view, "LeftPane");
                var right = Find<Border>(view, "RightPane");
                var overlay = Find<Panel>(view, "SplitOverlay");
                overlay.IsVisible.Should().BeTrue();
                right.Bounds.Should().Be(left.Bounds);
                Clip(right).Should().Be(new Rect(overlay.Bounds.Width / 2, 0, overlay.Bounds.Width / 2, overlay.Bounds.Height));

                vm.SplitPosition = 0.25;
                window.UpdateLayout();
                Clip(right).X.Should().BeApproximately(overlay.Bounds.Width / 4, 1e-6);
                Find<Border>(view, "SplitDivider").Margin.Left.Should().BeApproximately(overlay.Bounds.Width / 4 - 1, 1e-6);

                vm.Layout = ComparisonPlaybackViewModel.SideBySideLayout;
                window.UpdateLayout();
                overlay.IsVisible.Should().BeFalse();
                right.Clip.Should().BeNull();
                Grid.GetColumn(right).Should().Be(1);
                right.Bounds.X.Should().BeGreaterThan(left.Bounds.Right);
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task DraggingAndArrowKeys_MoveTheDivider()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var vm = Comparison();
            vm.Layout = ComparisonPlaybackViewModel.SliderLayout;
            var (window, view) = Show(vm);
            try
            {
                var overlay = Find<Panel>(view, "SplitOverlay");
                Point At(double fraction) => overlay.TranslatePoint(
                    new Point(overlay.Bounds.Width * fraction, overlay.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("Overlay is not attached.");

                window.MouseDown(At(0.2), MouseButton.Left);
                vm.SplitPosition.Should().BeApproximately(0.2, 1e-6);
                window.MouseMove(At(0.7));
                vm.SplitPosition.Should().BeApproximately(0.7, 1e-6);
                window.MouseUp(At(0.7), MouseButton.Left);
                window.MouseMove(At(0.9));
                vm.SplitPosition.Should().BeApproximately(0.7, 1e-6, "moving without a pressed button must not drag");

                overlay.IsFocused.Should().BeTrue();
                window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
                vm.SplitPosition.Should().BeApproximately(0.69, 1e-6);
                window.KeyPress(Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight, null);
                vm.SplitPosition.Should().BeApproximately(0.79, 1e-6);
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData("100%", true)]
    [InlineData("Fit", false)]
    public async Task SliderLayout_ScalesSmallerVariantOntoSourceArea_AtNativeZoom(string zoom, bool aligned)
    {
        await fixture.Session.Dispatch(() =>
        {
            using var vm = Comparison();
            vm.Layout = ComparisonPlaybackViewModel.SliderLayout;
            vm.Zoom = zoom;
            var (window, view) = Show(vm);
            try
            {
                vm.FramesToDisplay = new ComparisonFrames(Frame(64, 36), Frame(32, 18));
                window.UpdateLayout();
                var right = Find<Image>(view, "RightFrame");
                right.Stretch.Should().Be(Stretch.Uniform);
                if (aligned)
                {
                    right.Bounds.Size.Should().Be(Find<Image>(view, "LeftFrame").Bounds.Size);
                    right.Width.Should().Be(64);
                }
                else right.Width.Should().Be(double.NaN);

                vm.Layout = ComparisonPlaybackViewModel.SideBySideLayout;
                window.UpdateLayout();
                right.Width.Should().Be(double.NaN);
                right.Stretch.Should().Be(aligned ? Stretch.None : Stretch.Uniform);
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None);
    }

    private static ComparisonPlaybackViewModel Comparison()
    {
        var vm = new ComparisonPlaybackViewModel(null);
        vm.Load(new ComparisonTrack("left", 0), new ComparisonTrack("right", 0), 12, 25);
        return vm;
    }
    private static (Window Window, ComparisonPlaybackView View) Show(ComparisonPlaybackViewModel vm)
    {
        var view = new ComparisonPlaybackView { DataContext = vm };
        var window = new Window { Content = view, Width = 800, Height = 500 };
        window.Show();
        window.UpdateLayout();
        return (window, view);
    }
    private static T Find<T>(Control view, string name) where T : Control =>
        view.FindControl<T>(name) ?? throw new InvalidOperationException($"{name} missing.");
    private static Rect Clip(Visual visual) =>
        (visual.Clip as RectangleGeometry)?.Rect ?? throw new InvalidOperationException("Pane is not clipped.");
    private static ComparisonVideoFrame Frame(int width, int height)
    {
        var length = width * height * 4;
        var pixels = MemoryPool<byte>.Shared.Rent(length);
        pixels.Memory.Span[..length].Fill(255);
        return new ComparisonVideoFrame(width, height, 0, pixels);
    }
}
