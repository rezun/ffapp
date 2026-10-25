using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using FFApp.Core.Services;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FFApp.ViewModels;

namespace FFApp.Views;

public sealed partial class ComparisonPlaybackView : UserControl
{
    private ComparisonPlaybackViewModel? _vm;
    private WriteableBitmap? _leftFrame;
    private WriteableBitmap? _rightFrame;
    private static readonly double SplitStep = 0.01;
    private static readonly double LargeSplitStep = 0.1;
    private static readonly Thickness LeftPaneMargin = new(0, 0, 3, 0);
    private static readonly Thickness RightPaneMargin = new(3, 0, 0, 0);
    public static readonly StyledProperty<Control?> OverviewProperty =
        AvaloniaProperty.Register<ComparisonPlaybackView, Control?>(nameof(Overview));
    /// <summary>Optional strip between the video and the sample scrubber, e.g. the source timeline.</summary>
    public Control? Overview { get => GetValue(OverviewProperty); set => SetValue(OverviewProperty, value); }
    public ComparisonPlaybackView()
    {
        InitializeComponent();
        SplitOverlay.PointerPressed += OnSplitPointerPressed;
        SplitOverlay.PointerMoved += OnSplitPointerMoved;
        SplitOverlay.PointerReleased += (_, e) => e.Pointer.Capture(null);
        SplitOverlay.DoubleTapped += (_, _) => _vm?.ResetSplit();
        SplitOverlay.KeyDown += OnSplitKeyDown;
        SplitOverlay.SizeChanged += (_, _) => UpdateSplit();
        DataContextChanged += OnContextChanged;
        DetachedFromVisualTree += (_, _) => Detach();
        AttachedToVisualTree += (_, _) => Attach();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == OverviewProperty) OverviewHost.Content = change.GetNewValue<Control?>();
    }

    private void OnContextChanged(object? sender, EventArgs e) { Detach(); Attach(); }
    private void Attach()
    {
        if (_vm is not null) return;
        _vm = DataContext as ComparisonPlaybackViewModel;
        if (_vm is null) return;
        _vm.PropertyChanged += OnPlaybackChanged;
        UpdateLayoutMode();
        UpdateFrames();
        UpdateFrameZoom();
    }
    private void Detach()
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnPlaybackChanged;
        }
        ClearFrames();
        _vm = null;
    }
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComparisonPlaybackViewModel.IsSliderLayout)) UpdateLayoutMode();
        if (e.PropertyName == nameof(ComparisonPlaybackViewModel.SplitPosition)) UpdateSplit();
        if (e.PropertyName == nameof(ComparisonPlaybackViewModel.FramesToDisplay)) UpdateFrames();
        if (e.PropertyName == nameof(ComparisonPlaybackViewModel.Zoom)) UpdateFrameZoom();
    }
    private void UpdateFrames()
    {
        if (_vm?.FramesToDisplay is not { } frames) { ClearFrames(); return; }
        CopyFrame(ref _leftFrame, frames.Left);
        if (frames.Right is { } right) CopyFrame(ref _rightFrame, right);
        else { _rightFrame?.Dispose(); _rightFrame = null; }
        LeftFrame.Source = _leftFrame;
        RightFrame.Source = _rightFrame;
        AlignRightFrame();
        // WriteableBitmap pixels change in place during playback.
        LeftFrame.InvalidateVisual();
        RightFrame.InvalidateVisual();
    }
    private static unsafe void CopyFrame(ref WriteableBitmap? bitmap, ComparisonVideoFrame frame)
    {
        var dpi = new Vector(96 / frame.PixelAspectRatio, 96);
        if (bitmap?.PixelSize != new PixelSize(frame.Width, frame.Height) || bitmap.Dpi != dpi)
        {
            bitmap?.Dispose();
            bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), dpi,
                PixelFormat.Bgra8888, AlphaFormat.Opaque);
        }
        if (bitmap is null) return;
        using var buffer = bitmap.Lock();
        var target = new Span<byte>((void*)buffer.Address, checked(buffer.RowBytes * frame.Height));
        for (var row = 0; row < frame.Height; row++)
            frame.Pixels.Span.Slice(row * frame.Width * 4, frame.Width * 4)
                .CopyTo(target.Slice(row * buffer.RowBytes));
    }
    private void ClearFrames()
    {
        LeftFrame.Source = null;
        RightFrame.Source = null;
        _leftFrame?.Dispose();
        _rightFrame?.Dispose();
        _leftFrame = null;
        _rightFrame = null;
    }
    private void UpdateFrameZoom()
    {
        foreach (var image in new[] { LeftFrame, RightFrame })
        {
            image.Stretch = _vm?.Zoom == "Fit" ? Stretch.Uniform : Stretch.None;
            var scale = _vm?.Zoom == "200%" ? 2 : 1;
            image.RenderTransform = new ScaleTransform(scale, scale);
        }
        AlignRightFrame();
    }
    /// <summary>
    /// The slider overlays B on A, so B must cover exactly A's area even when its resolution
    /// differs (e.g. a downscaled variant shown at 100%). Fit mode already stretches both to the pane.
    /// </summary>
    private void AlignRightFrame()
    {
        var fit = _vm?.Zoom == "Fit";
        var size = _vm?.IsSliderLayout == true && !fit ? _leftFrame?.Size : null;
        RightFrame.Width = size?.Width ?? double.NaN;
        RightFrame.Height = size?.Height ?? double.NaN;
        RightFrame.Stretch = fit || size is not null ? Stretch.Uniform : Stretch.None;
    }
    private void UpdateLayoutMode()
    {
        var slider = _vm?.IsSliderLayout == true;
        LeftPane.IsVisible = _vm?.IsSourcePreview == true || _vm?.Layout != ComparisonPlaybackViewModel.BOnlyLayout;
        RightPane.IsVisible = _vm?.IsSourcePreview != true && _vm?.Layout != ComparisonPlaybackViewModel.AOnlyLayout;
        // The slider stacks both panes over the full width; B is clipped to the right of the divider.
        Grid.SetColumnSpan(LeftPane, slider ? 2 : 1);
        Grid.SetColumn(RightPane, slider ? 0 : 1);
        Grid.SetColumnSpan(RightPane, slider ? 2 : 1);
        LeftPane.Margin = slider ? default : LeftPaneMargin;
        RightPane.Margin = slider ? default : RightPaneMargin;
        VideoGrid.ColumnDefinitions[0].Width = LeftPane.IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        VideoGrid.ColumnDefinitions[1].Width = RightPane.IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        AlignRightFrame();
        UpdateSplit();
    }
    private void UpdateSplit()
    {
        if (_vm?.IsSliderLayout != true) { RightPane.Clip = null; return; }
        var size = SplitOverlay.Bounds.Size;
        var x = _vm.SplitPosition * size.Width;
        RightPane.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, size.Width - x), size.Height));
        SplitDivider.Margin = new Thickness(x - SplitDivider.Width / 2, 0, 0, 0);
        SplitHandle.Margin = new Thickness(x - SplitHandle.Width / 2, 0, 0, 0);
    }
    private void SetSplitFromPointer(PointerEventArgs e)
    {
        var width = SplitOverlay.Bounds.Width;
        if (_vm is null || width <= 0) return;
        _vm.SplitPosition = e.GetPosition(SplitOverlay).X / width;
    }
    private void OnSplitPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(SplitOverlay).Properties.IsLeftButtonPressed) return;
        e.Pointer.Capture(SplitOverlay);
        SplitOverlay.Focus(NavigationMethod.Pointer);
        SetSplitFromPointer(e);
        e.Handled = true;
    }
    private void OnSplitPointerMoved(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(e.Pointer.Captured, SplitOverlay)) SetSplitFromPointer(e);
    }
    private void OnSplitKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null || e.Key is not (Key.Left or Key.Right)) return;
        var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? LargeSplitStep : SplitStep;
        _vm.MoveSplit(e.Key == Key.Left ? -step : step);
        e.Handled = true;
    }
}
