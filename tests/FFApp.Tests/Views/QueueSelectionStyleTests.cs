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
    // The selected-row override ignores the state color, so one plain state and Failed
    // (which adds the failure line) cover every row shape in both themes.
    [Theory]
    [InlineData(false, JobState.Pending)]
    [InlineData(false, JobState.Failed)]
    [InlineData(true, JobState.Pending)]
    [InlineData(true, JobState.Failed)]
    public async Task SelectedRow_UsesThemeSelectionColors_AndRestoresColorsOnDeselect(
        bool dark, JobState state)
    {
        await fixture.Session.Dispatch(() => VerifySelectionColors(dark, state), CancellationToken.None);
    }

    private static void VerifySelectionColors(bool dark, JobState state)
    {
        var media = new TestMediaInfoBuilder().Build();
        var queued = new QueuedJob(new TestJobDefinitionBuilder().Build(), media, [], new ValidationResult());
        var item = new JobItemViewModel("selection-test", media, queued, "source.mkv")
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
                ColorOf(text.Foreground).Should().Be(ColorOf(referencePresenter.Foreground));
            ColorOf(progress.Foreground).Should().Be(ColorOf(referencePresenter.Foreground));
            ColorOf(progress.Background).Should().Be(background);
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
                ColorOf(text.Foreground).Should().Be(ColorOf(referencePresenter.Foreground));
            ColorOf(progress.Foreground).Should().Be(ColorOf(referencePresenter.Foreground));
            ColorOf(progress.Background).Should().Be(hoveredBackground);

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
}
