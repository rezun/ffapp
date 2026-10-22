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

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (RangeEnd <= RangeStart || Bounds.Width <= 0) return;
        var background = this.TryFindResource("BorderSubtleBrush", out var border) && border is IBrush brush ? brush : Brushes.Gray;
        IBrush accent = this.TryFindResource("SystemAccentColor", out var color) && color is Color c ? new SolidColorBrush(c) : Brushes.DodgerBlue;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.FillRectangle(background, new Rect(0, Bounds.Height / 2 - 2, Bounds.Width, 4));
        foreach (var sample in Samples)
        {
            if (!double.IsFinite(sample.StartSeconds) || !double.IsFinite(sample.DurationSeconds)) continue;
            var left = Math.Clamp((sample.StartSeconds - RangeStart) / (RangeEnd - RangeStart), 0, 1) * Bounds.Width;
            var right = Math.Clamp((sample.EndSeconds - RangeStart) / (RangeEnd - RangeStart), 0, 1) * Bounds.Width;
            var selected = string.Equals(sample.Id, SelectedId, StringComparison.Ordinal);
            context.FillRectangle(selected ? accent : background,
                new Rect(left, selected ? 2 : 6, Math.Max(5, right - left), Math.Max(1, Bounds.Height - (selected ? 4 : 12))));
        }
    }

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
