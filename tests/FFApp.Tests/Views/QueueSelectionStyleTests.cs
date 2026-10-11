using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FFApp.Core.Models;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FFApp.Views;
using FluentAssertions;

namespace FFApp.Tests.Views;

public static class QueueTestApplication
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class QueueTestSession : IDisposable
{
    public HeadlessUnitTestSession Session { get; } = HeadlessUnitTestSession.StartNew(
        typeof(QueueTestApplication), AvaloniaTestIsolationLevel.PerAssembly);

    public void Dispose() => Session.Dispose();
}

[CollectionDefinition("Queue UI")]
public sealed class QueueTestCollection : ICollectionFixture<QueueTestSession>;

[Collection("Queue UI")]
public sealed class QueueSelectionStyleTests(QueueTestSession fixture)
{
    // Fluent's default blue with white text yields approximately 4.499:1.
    private static readonly double MinimumThemeTextContrast = 4.49;

    [Theory]
    [InlineData(false, JobState.Pending)]
    [InlineData(false, JobState.Running)]
    [InlineData(false, JobState.Completed)]
    [InlineData(false, JobState.Failed)]
    [InlineData(false, JobState.Canceled)]
    [InlineData(true, JobState.Pending)]
    [InlineData(true, JobState.Running)]
    [InlineData(true, JobState.Completed)]
    [InlineData(true, JobState.Failed)]
    [InlineData(true, JobState.Canceled)]
    public async Task SelectedRow_HasReadableTextAndDistinctProgress_AndRestoresColorsOnDeselect(
        bool dark, JobState state)
    {
        await fixture.Session.Dispatch(() => VerifySelectionColors(dark, state), CancellationToken.None);
    }

    private static void VerifySelectionColors(bool dark, JobState state)
    {
        var media = new TestMediaInfoBuilder().Build();
        var queued = new QueuedJob(new TestJobDefinitionBuilder().Build(), media, [], new ValidationResult());
        var item = new JobItemViewModel("contrast-test", media, queued, "source.mkv")
        {
            State = state,
            Percentage = 28.8,
            ProgressText = "28.8% | 2.1x | 16:35 | ~41:01 left",
        };
        if (state == JobState.Failed)
            item.UpdateFailure("libopus cannot encode audio at 44.1 kHz.");
        var view = new JobListPanelView();
        var list = view.FindControl<ListBox>("JobList")
            ?? throw new InvalidOperationException("Queue list is missing.");
        list.ItemsSource = new[] { item };
        list.IsVisible = true;
        var referenceList = new ListBox { ItemsSource = new[] { "Reference" }, SelectedIndex = 0 };
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,160") };
        content.Children.Add(view);
        Grid.SetColumn(referenceList, 1);
        content.Children.Add(referenceList);
        var window = new Window
        {
            Content = content,
            Width = 660,
            Height = 600,
            RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light,
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            var row = list.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Queue row was not realized.");
            var texts = row.GetVisualDescendants().OfType<TextBlock>()
                .Where(text => string.Equals(text.Text, item.DisplayLabel, StringComparison.Ordinal)
                    || string.Equals(text.Text, item.State.ToString(), StringComparison.Ordinal)
                    || string.Equals(text.Text, item.ProgressText, StringComparison.Ordinal)
                    || (item.HasFailure && string.Equals(text.Text, item.Failure?.Message, StringComparison.Ordinal)))
                .ToArray();
            texts.Should().HaveCount(item.HasFailure ? 4 : 3);
            var progress = row.GetVisualDescendants().OfType<ProgressBar>().Single();
            var originalTextColors = texts.Select(text => ColorOf(text.Foreground)).ToArray();
            var originalProgressColor = ColorOf(progress.Foreground);

            list.SelectedIndex = 0;
            window.UpdateLayout();

            var presenter = row.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(control => control.Name == "PART_ContentPresenter");
            var background = ColorOf(presenter.Background);
            var referenceRow = referenceList.ContainerFromIndex(0) as ListBoxItem
                ?? throw new InvalidOperationException("Reference row was not realized.");
            var referencePresenter = referenceRow.GetVisualDescendants().OfType<ContentPresenter>()
                .Single(control => control.Name == "PART_ContentPresenter");
            background.Should().Be(ColorOf(referencePresenter.Background),
                "the queue should use the standard selection background");
            foreach (var text in texts)
            {
                ColorOf(text.Foreground).Should().Be(ColorOf(referencePresenter.Foreground));
                Contrast(ColorOf(text.Foreground), background).Should().BeGreaterThanOrEqualTo(MinimumThemeTextContrast);
            }
            Contrast(ColorOf(progress.Foreground), background).Should().BeGreaterThanOrEqualTo(3);
            Contrast(ColorOf(progress.Foreground), ColorOf(progress.Background))
                .Should().BeGreaterThanOrEqualTo(3);
            progress.Opacity.Should().Be(1);

            // Hovering must retain the standard selection appearance and readable content.
            var referenceLocation = referenceRow.TranslatePoint(new Point(10, 10), window)
                ?? throw new InvalidOperationException("Reference row has no window position.");
            window.MouseMove(referenceLocation);
            window.UpdateLayout();
            var hoveredBackground = ColorOf(referencePresenter.Background);
            var location = row.TranslatePoint(new Point(10, 10), window)
                ?? throw new InvalidOperationException("Queue row has no window position.");
            window.MouseMove(location);
            window.UpdateLayout();
            ColorOf(presenter.Background).Should().Be(hoveredBackground);
            foreach (var text in texts)
                Contrast(ColorOf(text.Foreground), hoveredBackground).Should().BeGreaterThanOrEqualTo(MinimumThemeTextContrast);
            Contrast(ColorOf(progress.Foreground), ColorOf(progress.Background))
                .Should().BeGreaterThanOrEqualTo(3);

            list.SelectedIndex = -1;
            window.UpdateLayout();
            texts.Select(text => ColorOf(text.Foreground)).Should().Equal(originalTextColors);
            ColorOf(progress.Foreground).Should().Be(originalProgressColor);
        }
        finally
        {
            window.Close();
        }
    }

    private static Color ColorOf(IBrush? brush) =>
        (brush as ISolidColorBrush)?.Color
        ?? throw new InvalidOperationException("Expected an opaque solid color brush.");

    private static double Contrast(Color first, Color second)
    {
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05)
            / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }

    private static double Luminance(Color color) =>
        0.2126 * LinearChannel(color.R)
        + 0.7152 * LinearChannel(color.G)
        + 0.0722 * LinearChannel(color.B);

    private static double LinearChannel(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }
}
