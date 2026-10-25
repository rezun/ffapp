using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using FFApp.Core.Models;

namespace FFApp.Controls;

public sealed class ComparisonTimeline : Control
{
    public static readonly StyledProperty<IReadOnlyList<ComparisonSample>> SamplesProperty =
        AvaloniaProperty.Register<ComparisonTimeline, IReadOnlyList<ComparisonSample>>(nameof(Samples), Array.Empty<ComparisonSample>());
    public static readonly StyledProperty<double> RangeStartProperty = AvaloniaProperty.Register<ComparisonTimeline, double>(nameof(RangeStart));
    public static readonly StyledProperty<double> RangeEndProperty = AvaloniaProperty.Register<ComparisonTimeline, double>(nameof(RangeEnd), 1);
    public static readonly StyledProperty<string?> SelectedIdProperty = AvaloniaProperty.Register<ComparisonTimeline, string?>(nameof(SelectedId));
    public IReadOnlyList<ComparisonSample> Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public double RangeStart { get => GetValue(RangeStartProperty); set => SetValue(RangeStartProperty, value); }
    public double RangeEnd { get => GetValue(RangeEndProperty); set => SetValue(RangeEndProperty, value); }
    public string? SelectedId { get => GetValue(SelectedIdProperty); set => SetValue(SelectedIdProperty, value); }
    public event EventHandler<string>? SampleSelected;

    static ComparisonTimeline() => AffectsRender<ComparisonTimeline>(SamplesProperty, RangeStartProperty, RangeEndProperty, SelectedIdProperty);

    private static readonly double TrackThickness = 2;
    private static readonly double MarkerWidth = 2;
    private static readonly double SelectedMarkerWidth = 4;
    private static readonly double LabelFontSize = 10;
    private static readonly double LabelGap = 2;

    /// <summary>
    /// Draws the source range as a thin track with one numbered marker per sample. Samples are a few
    /// seconds of a feature-length source, so they are drawn as markers rather than to scale.
    /// </summary>
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (RangeEnd <= RangeStart || Bounds.Width <= 0) return;
        var track = Brush("BorderDefaultBrush", Brushes.Gray);
        var marker = Brush("TextSecondaryBrush", Brushes.Gray);
        IBrush accent = this.TryFindResource("SystemAccentColor", ActualThemeVariant, out var color) && color is Color c
            ? new SolidColorBrush(c) : Brushes.DodgerBlue;
        var typeface = new Typeface(FontFamily.Default);
        var labelHeight = LabelFontSize + 3;
        var trackTop = labelHeight + LabelGap + (Bounds.Height - labelHeight - LabelGap) / 2 - TrackThickness / 2;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.FillRectangle(track, new Rect(0, trackTop, Bounds.Width, TrackThickness));
        for (var index = 0; index < Samples.Count; index++)
        {
            var sample = Samples[index];
            if (!double.IsFinite(sample.StartSeconds) || !double.IsFinite(sample.DurationSeconds)) continue;
            var x = Math.Clamp((sample.StartSeconds - RangeStart) / (RangeEnd - RangeStart), 0, 1) * Bounds.Width;
            var selected = string.Equals(sample.Id, SelectedId, StringComparison.Ordinal);
            var width = selected ? SelectedMarkerWidth : MarkerWidth;
            var left = Math.Clamp(x - width / 2, 0, Math.Max(0, Bounds.Width - width));
            var markerTop = labelHeight + LabelGap;
            context.FillRectangle(selected ? accent : marker, new Rect(left, markerTop, width, Bounds.Height - markerTop), 1);
            var label = new FormattedText((index + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, LabelFontSize, selected ? accent : marker);
            if (selected) label.SetFontWeight(FontWeight.SemiBold);
            var labelLeft = Math.Clamp(left + width / 2 - label.Width / 2, 0, Math.Max(0, Bounds.Width - label.Width));
            context.DrawText(label, new Point(labelLeft, 0));
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Bounds.Width <= 0 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var time = RangeStart + Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1) * (RangeEnd - RangeStart);
        var nearest = Samples.Where(s => double.IsFinite(s.StartSeconds))
            .MinBy(s => Math.Abs(s.StartSeconds + s.DurationSeconds / 2 - time));
        if (nearest is not null) SampleSelected?.Invoke(this, nearest.Id);
        e.Handled = true;
    }
}
