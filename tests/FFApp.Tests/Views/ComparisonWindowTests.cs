using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FFApp.Controls;
using FFApp.Tests.ViewModels;
using FFApp.Tests.Builders;
using FFApp.Views;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class ComparisonWindowTests(QueueTestSession fixture)
{
    [Fact]
    public async Task MaximizeVideos_ReclaimsPanelSpace_AndRestoresResizedResults()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var harness = new ComparisonTestHarness();
            await harness.LoadAsync();
            var vm = harness.ViewModel;
            await vm.RunAllCommand.ExecuteAsync(null);
            vm.ShowResultsCommand.Execute(null);
            var window = new ComparisonWindow { DataContext = vm };
            window.Show();
            try
            {
                window.UpdateLayout();
                var button = window.FindControl<Button>("TogglePreviewSizeButton")
                    ?? throw new InvalidOperationException("Preview size button missing.");
                var samples = window.FindControl<Border>("SamplePanel")
                    ?? throw new InvalidOperationException("Sample panel missing.");
                var results = window.FindControl<Border>("ResultsPanel")
                    ?? throw new InvalidOperationException("Results panel missing.");
                var grid = window.FindControl<Grid>("CompareGrid")
                    ?? throw new InvalidOperationException("Compare grid missing.");
                var preview = window.FindControl<ComparisonPlaybackView>("SamplePlayback")
                    ?? throw new InvalidOperationException("Preview missing.");
                grid.RowDefinitions[3].Height = new GridLength(180);
                window.UpdateLayout();
                var originalSize = preview.Bounds.Size;
                var selectedSample = vm.SelectedSample;
                var leftChoice = vm.LeftChoice;
                var rightChoice = vm.RightChoice;
                window.GetVisualDescendants().OfType<Expander>()
                    .Should().NotContain(e => Equals(e.Header, "Selected result details"));

                button.Command.Should().BeSameAs(vm.TogglePreviewSizeCommand);
                button.Command?.Execute(null);
                window.UpdateLayout();
                samples.IsVisible.Should().BeFalse();
                results.IsVisible.Should().BeFalse();
                grid.RowDefinitions[3].ActualHeight.Should().Be(0);
                preview.Bounds.Width.Should().BeGreaterThan(originalSize.Width);
                preview.Bounds.Height.Should().BeGreaterThan(originalSize.Height);
                button.IsEffectivelyVisible.Should().BeTrue();
                button.Content.Should().Be("Restore panels");
                vm.SelectedSample.Should().BeSameAs(selectedSample);
                vm.LeftChoice.Should().BeSameAs(leftChoice);
                vm.RightChoice.Should().BeSameAs(rightChoice);

                button.Command?.Execute(null);
                window.UpdateLayout();
                samples.IsVisible.Should().BeTrue();
                results.IsVisible.Should().BeTrue();
                grid.RowDefinitions[3].ActualHeight.Should().Be(180);
                preview.Bounds.Size.Should().Be(originalSize);

                button.Command?.Execute(null);
                vm.ShowVariantsCommand.Execute(null);
                window.UpdateLayout();
                samples.IsVisible.Should().BeTrue();
                button.IsVisible.Should().BeFalse();
            }
            finally { await window.CloseSavedAsync(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RawFrames_UpdateBothSurfaces_ReuseBitmaps_KeepZoom_AndClearOnReset()
    {
        await fixture.Session.Dispatch(() =>
        {
            using var vm = new ComparisonPlaybackViewModel(null);
            vm.Load(new ComparisonTrack("source", 100), new ComparisonTrack("clip", 2), 12, 24);
            var view = new ComparisonPlaybackView { DataContext = vm };
            var window = new Window { Content = view };
            window.Show();
            try
            {
                vm.FramesToDisplay = new ComparisonFrames(ComparisonPlaybackViewModelTests.Frame(0), ComparisonPlaybackViewModelTests.Frame(0));
                var left = view.FindControl<Image>("LeftFrame") ?? throw new InvalidOperationException("Missing left image.");
                var right = view.FindControl<Image>("RightFrame") ?? throw new InvalidOperationException("Missing right image.");
                left.Source.Should().NotBeNull();
                right.Source.Should().NotBeNull();
                left.IsVisible.Should().BeTrue();
                right.IsVisible.Should().BeTrue();
                view.FindControl<Control>("LeftVideo").Should().BeNull();
                view.FindControl<Control>("RightVideo").Should().BeNull();
                var originalLeft = left.Source;
                var originalRight = right.Source;
                var newLeft = ComparisonPlaybackViewModelTests.Frame(.04);
                var newRight = ComparisonPlaybackViewModelTests.Frame(.04);
                newLeft.Pixels.Span.Fill(42);
                newRight.Pixels.Span.Fill(84);
                vm.FramesToDisplay = new ComparisonFrames(newLeft, newRight);
                left.Source.Should().BeSameAs(originalLeft);
                right.Source.Should().BeSameAs(originalRight);
                vm.Zoom = "200%";
                left.Stretch.Should().Be(Stretch.None);
                left.RenderTransform.Should().BeOfType<ScaleTransform>().Which.ScaleX.Should().Be(2);
                right.RenderTransform.Should().BeOfType<ScaleTransform>().Which.ScaleX.Should().Be(2);
                vm.Reset();
                left.Source.Should().BeNull();
                right.Source.Should().BeNull();
                left.IsVisible.Should().BeFalse();
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Window_PreservesOuterMargins_AndReservesFooterSpaceOnlyWhenNeeded()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var harness = new ComparisonTestHarness();
            await harness.LoadAsync();
            var vm = harness.ViewModel;
            var window = new ComparisonWindow { DataContext = vm };
            window.Show();
            try
            {
                window.UpdateLayout();
                var layout = window.FindControl<Grid>("ComparisonLayout")
                    ?? throw new InvalidOperationException("Window layout missing.");
                var footer = window.FindControl<StackPanel>("ComparisonStatusPanel")
                    ?? throw new InvalidOperationException("Status panel missing.");
                layout.Margin.Should().Be(new Thickness(16));
                footer.IsVisible.Should().BeFalse();
                layout.RowDefinitions[3].ActualHeight.Should().Be(0);

                vm.Status = "Unable to save comparison.";
                window.UpdateLayout();
                footer.IsVisible.Should().BeTrue();
                layout.RowDefinitions[3].ActualHeight.Should().BeGreaterThan(0);

                vm.Status = "";
                vm.IsRunning = true;
                window.UpdateLayout();
                footer.IsVisible.Should().BeTrue();
                footer.Children.OfType<ProgressBar>().Single().IsVisible.Should().BeTrue();
                footer.Children.OfType<TextBlock>().Single().IsVisible.Should().BeFalse();

                vm.IsRunning = false;
                window.UpdateLayout();
                footer.IsVisible.Should().BeFalse();
                layout.RowDefinitions[3].ActualHeight.Should().Be(0);
            }
            finally
            {
                vm.IsRunning = false;
                vm.Status = "";
                await window.CloseSavedAsync();
            }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lists_UseSelectionForegroundForSecondaryText_AndRestoreOnDeselect(bool dark)
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var harness = new ComparisonTestHarness();
            await harness.LoadAsync();
            var window = new ComparisonWindow
            {
                DataContext = harness.ViewModel,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            window.Show();
            try
            {
                for (var step = 0; step < 2; step++)
                {
                    harness.ViewModel.SelectedTab = step;
                    window.UpdateLayout();
                    var list = window.FindControl<ListBox>(step == 0 ? "SampleList" : "VariantList")
                        ?? throw new InvalidOperationException("List missing.");
                    list.SelectedIndex = -1;
                    window.UpdateLayout();
                    var row = list.ContainerFromIndex(0) as ListBoxItem
                        ?? throw new InvalidOperationException("List row missing.");
                    var text = row.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("secondary"));
                    var original = (text.Foreground as ISolidColorBrush)?.Color;
                    original.Should().NotBeNull();
                    list.SelectedIndex = 0;
                    window.UpdateLayout();
                    var presenter = row.GetVisualDescendants().OfType<ContentPresenter>()
                        .Single(p => p.Name == "PART_ContentPresenter");
                    (text.Foreground as ISolidColorBrush)?.Color.Should().Be((presenter.Foreground as ISolidColorBrush)?.Color);
                    var point = row.TranslatePoint(new Point(10, 10), window)
                        ?? throw new InvalidOperationException("List row not attached.");
                    window.MouseMove(point);
                    window.UpdateLayout();
                    (text.Foreground as ISolidColorBrush)?.Color.Should().Be((presenter.Foreground as ISolidColorBrush)?.Color);
                    list.SelectedIndex = -1;
                    window.UpdateLayout();
                    (text.Foreground as ISolidColorBrush)?.Color.Should().Be(original);
                }
            }
            finally { await window.CloseSavedAsync(); }
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task Window_BindsSamplesVariantsAndResults_AndTimelineSelectsSample()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var harness = new ComparisonTestHarness();
            await harness.LoadAsync();
            var vm = harness.ViewModel;
            vm.SampleCount = 2;
            vm.DistributeCommand.Execute(null);
            vm.AddGrainVariantsCommand.Execute(null);
            var window = new ComparisonWindow { DataContext = vm, Width = 1480, Height = 960 };
            window.Show();
            try
            {
                window.UpdateLayout();
                var samples = window.FindControl<ListBox>("SampleList") ?? throw new InvalidOperationException("Sample list missing.");
                samples.ItemCount.Should().Be(2);
                var timeline = window.FindControl<ComparisonTimeline>("SampleTimeline") ?? throw new InvalidOperationException("Timeline missing.");
                timeline.Samples.Should().HaveCount(2);
                timeline.Bounds.Width.Should().BeGreaterThan(1000);
                var point = timeline.TranslatePoint(new Point(timeline.Bounds.Width * 0.75, timeline.Bounds.Height / 2), window)
                    ?? throw new InvalidOperationException("Timeline is not attached.");
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                vm.SelectedSample.Should().BeSameAs(vm.Samples[1]);
                samples.SelectedItem.Should().BeSameAs(vm.Samples[1]);

                var start = window.FindControl<NumericUpDown>("SampleStartInput") ?? throw new InvalidOperationException("Start input missing.");
                start.Text = "00:01:23.456";
                vm.Samples[1].StartSeconds.Should().Be(83.456);
                start.Text = "00:99:00";
                vm.Samples[1].StartSeconds.Should().Be(83.456, "invalid timecodes must not change the sample");
                start.Text = "00:01:23.456";
                vm.SelectedTab = 1;
                window.UpdateLayout();
                var variants = window.FindControl<ListBox>("VariantList") ?? throw new InvalidOperationException("Variant list missing.");
                variants.ItemCount.Should().Be(3);
                variants.SelectedItem = vm.Variants[2];
                vm.SelectedVariant.Should().BeSameAs(vm.Variants[2]);
                vm.Editor.IsVideoModeEditable.Should().BeFalse();

                await vm.RunAllCommand.ExecuteAsync(null);
                vm.ShowResultsCommand.Execute(null);
                window.UpdateLayout();
                var table = window.FindControl<ItemsControl>("ResultsTable") ?? throw new InvalidOperationException("Results missing.");
                table.ItemCount.Should().Be(2);
                vm.Rows.Should().OnlyContain(r => r.Cells.All(c => c.Clip != null));
                (window.FindControl<Button>("PrimaryComparisonButton")?.Command).Should().BeSameAs(vm.PrimaryActionCommand);
                (await window.CloseSavedAsync()).Should().BeTrue();
            }
            finally
            {
                if (window.IsVisible) await window.CloseSavedAsync();
            }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NumericFields_CenterText_HideInnerScrollbars_AndKeepValueSpinnerWorking(bool dark)
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var harness = new ComparisonTestHarness();
            await harness.LoadAsync();
            var window = new ComparisonWindow
            {
                DataContext = harness.ViewModel,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                foreach (var name in new[] { "SampleLengthInput", "SampleDurationInput" })
                {
                    var field = window.FindControl<NumericUpDown>(name)
                        ?? throw new InvalidOperationException("Duration input missing.");
                    field.Text.Should().Be("12");
                    field.FormatString.Should().Be("0");
                    field.Increment.Should().Be(1);
                    field.Value = 15;
                    field.Text.Should().Be("15");
                }
                harness.ViewModel.SampleDuration.Should().Be(15);
                harness.ViewModel.SelectedSample?.DurationSeconds.Should().Be(15);
                var fields = window.GetVisualDescendants().OfType<NumericUpDown>().ToArray();
                fields.Should().HaveCount(4);
                foreach (var field in fields)
                {
                    var editor = field.GetVisualDescendants().OfType<TextBox>().Single();
                    editor.Focus();
                    window.UpdateLayout();
                    var scrollbars = editor.GetVisualDescendants().OfType<ScrollBar>().ToArray();
                    scrollbars.Should().NotBeEmpty();
                    scrollbars.Should().OnlyContain(bar => !bar.IsEffectivelyVisible);
                    var text = editor.GetVisualDescendants().OfType<TextPresenter>().Single();
                    var viewport = editor.GetVisualDescendants().OfType<ScrollViewer>().Single();
                    text.Bounds.Height.Should().BeLessThanOrEqualTo(viewport.Viewport.Height,
                        "the numeric text must fit without vertical clipping");
                    var textCenter = text.TranslatePoint(new Point(0, text.Bounds.Height / 2), field)
                        ?? throw new InvalidOperationException("Numeric text is not attached.");
                    textCenter.Y.Should().BeApproximately(field.Bounds.Height / 2, 0.75,
                        "the number must be vertically centered in its field");
                }
                var start = window.FindControl<NumericUpDown>("SampleStartInput")
                    ?? throw new InvalidOperationException("Start input missing.");
                var original = start.Value;
                var buttons = start.GetVisualDescendants().OfType<RepeatButton>().ToArray();
                var increase = buttons.Single(b => string.Equals(b.Name, "PART_IncreaseButton", StringComparison.Ordinal));
                var decrease = buttons.Single(b => string.Equals(b.Name, "PART_DecreaseButton", StringComparison.Ordinal));
                increase.IsEffectivelyVisible.Should().BeTrue();
                decrease.IsEffectivelyVisible.Should().BeTrue();
                increase.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                start.Value.Should().Be(original + start.Increment);
                decrease.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                start.Value.Should().Be(original);
            }
            finally { await window.CloseSavedAsync(); }
            return true;
        }, CancellationToken.None);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialWindow_ShowsSingleOriginalPreview_HidesEmptyResults_AndHighlightsNextAction(bool dark)
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var harness = new ComparisonTestHarness();
            await harness.LoadAsync();
            var vm = harness.ViewModel;
            var window = new ComparisonWindow
            {
                DataContext = vm, Width = 1480, Height = 960,
                RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                var results = window.FindControl<Border>("ResultsPanel") ?? throw new InvalidOperationException("Results panel missing.");
                results.IsVisible.Should().BeFalse();
                var compareGrid = window.FindControl<Grid>("CompareGrid") ?? throw new InvalidOperationException("Preview grid missing.");
                compareGrid.RowDefinitions[3].ActualHeight.Should().Be(0);
                var choices = window.FindControl<Grid>("ComparisonChoices") ?? throw new InvalidOperationException("Comparison selectors missing.");
                choices.IsVisible.Should().BeFalse();
                var preview = window.FindControl<ComparisonPlaybackView>("SamplePlayback") ?? throw new InvalidOperationException("Preview missing.");
                preview.FindControl<Border>("RightPane")?.IsVisible.Should().BeFalse();
                preview.FindControl<Border>("LeftPane")?.IsVisible.Should().BeTrue();
                harness.Playback.LeftTrack?.Path.Should().Be(harness.Context.MediaInfo.SourcePath);
                var next = window.FindControl<Button>("PrimaryComparisonButton") ?? throw new InvalidOperationException("Next action missing.");
                next.Content.Should().Be("Next: choose variants →");
                var presenter = next.GetVisualDescendants().OfType<ContentPresenter>()
                    .First(p => string.Equals(p.Name, "PART_ContentPresenter", StringComparison.Ordinal));
                var brush = presenter.Background.Should().BeAssignableTo<ISolidColorBrush>().Subject;
                brush.Color.B.Should().BeGreaterThan(brush.Color.R, "the primary action should be visibly blue");
                var play = preview.FindControl<Button>("PlaySampleButton") ?? throw new InvalidOperationException("Play button missing.");
                play.Content.Should().Be("Play sample");
                play.Classes.Should().Contain("accent");
                var back = preview.FindControl<Button>("SeekBackwardButton")
                    ?? throw new InvalidOperationException("Backward seek button missing.");
                back.Content.Should().Be("◀ 1 s");
                back.Command.Should().BeSameAs(harness.Playback.SeekBackwardCommand);
                ToolTip.GetTip(back).Should().Be("Seek back one second.");
                (window.FindControl<Button>("ResultsStepButton")?.IsEffectivelyEnabled).Should().BeFalse();
                await vm.PrimaryActionCommand.ExecuteAsync(null);
                window.UpdateLayout();
                next.Content.Should().Be("Create test clips (8)");
                harness.Execution.ReceivedCalls().Should().BeEmpty();
            }
            finally { await window.CloseSavedAsync(); }
            return true;
        }, CancellationToken.None);
    }
}
