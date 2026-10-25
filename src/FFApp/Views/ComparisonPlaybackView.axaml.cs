using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
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
    public ComparisonPlaybackView()
    {
        InitializeComponent();
        DataContextChanged += OnContextChanged;
        DetachedFromVisualTree += (_, _) => Detach();
        AttachedToVisualTree += (_, _) => Attach();
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
        if (e.PropertyName is nameof(ComparisonPlaybackViewModel.Layout) or nameof(ComparisonPlaybackViewModel.IsSourcePreview)) UpdateLayoutMode();
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
    }
    private void UpdateLayoutMode()
    {
        LeftPane.IsVisible = _vm?.IsSourcePreview == true || _vm?.Layout != "B only";
        RightPane.IsVisible = _vm?.IsSourcePreview != true && _vm?.Layout != "A only";
        VideoGrid.ColumnDefinitions[0].Width = LeftPane.IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        VideoGrid.ColumnDefinitions[1].Width = RightPane.IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }
}
