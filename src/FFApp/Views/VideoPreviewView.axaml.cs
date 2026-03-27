using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using FFApp.ViewModels;

namespace FFApp.Views;

public partial class VideoPreviewView : UserControl
{
    private VideoPreviewViewModel? _vm;

    public VideoPreviewView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;

        TimelineSlider.AddHandler(
            PointerPressedEvent, OnSliderPointerPressed, RoutingStrategies.Tunnel);
        TimelineSlider.AddHandler(
            PointerReleasedEvent, OnSliderPointerReleased, RoutingStrategies.Tunnel);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null)
            _vm.MediaPlayerChanged -= OnMediaPlayerChanged;

        _vm = DataContext as VideoPreviewViewModel;

        if (_vm is not null)
            _vm.MediaPlayerChanged += OnMediaPlayerChanged;
    }

    private void OnMediaPlayerChanged(object? sender, LibVLCSharp.Shared.MediaPlayer? player)
    {
        VideoView.MediaPlayer = player;
    }

    private void OnSliderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _vm?.BeginSeek();
    }

    private void OnSliderPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _vm?.EndSeek();
    }
}
