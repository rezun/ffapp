using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FFApp.ViewModels;

namespace FFApp.Views;

public sealed partial class ComparisonPlaybackView : UserControl
{
    private ComparisonPlaybackViewModel? _vm;
    private Bitmap? _leftFrame;
    private Bitmap? _rightFrame;
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
        _vm.PlayersChanged += OnPlayersChanged;
        _vm.PropertyChanged += OnPlaybackChanged;
        OnPlayersChanged(this, EventArgs.Empty);
        UpdateLayoutMode();
        UpdateFrames();
    }
    private void Detach()
    {
        if (_vm is not null)
        {
            _vm.PlayersChanged -= OnPlayersChanged;
            _vm.PropertyChanged -= OnPlaybackChanged;
        }
        LeftVideo.MediaPlayer = null;
        RightVideo.MediaPlayer = null;
        ClearFrames();
        _vm = null;
    }
    private void OnPlayersChanged(object? sender, EventArgs e)
    {
        LeftVideo.MediaPlayer = _vm?.LeftPlayer;
        RightVideo.MediaPlayer = _vm?.RightPlayer;
    }
    private void OnPlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComparisonPlaybackViewModel.Layout) or nameof(ComparisonPlaybackViewModel.IsSourcePreview)) UpdateLayoutMode();
        if (e.PropertyName == nameof(ComparisonPlaybackViewModel.FramesToDisplay)) UpdateFrames();
        if (e.PropertyName == nameof(ComparisonPlaybackViewModel.Zoom)) UpdateFrameZoom();
    }
    private void UpdateFrames()
    {
        var frames = _vm?.FramesToDisplay;
        Bitmap? left = null;
        Bitmap? right = null;
        try
        {
            if (frames is not null)
            {
                using var leftStream = new MemoryStream(frames.Left);
                left = new Bitmap(leftStream);
                if (frames.Right is { } bytes)
                {
                    using var rightStream = new MemoryStream(bytes);
                    right = new Bitmap(rightStream);
                }
            }
            ClearFrames();
            _leftFrame = left;
            _rightFrame = right;
            LeftFrame.Source = left;
            RightFrame.Source = right;
            UpdateFrameZoom();
        }
        catch
        {
            left?.Dispose();
            right?.Dispose();
            throw;
        }
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
