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
using FFApp.Tests.Builders;
using FFApp.Views;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.Views;

[Collection("Queue UI")]
public sealed class ComparisonWindowTests(QueueTestSession fixture)
{
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
                start.Value = 83;
                vm.Samples[1].StartSeconds.Should().Be(83);
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
