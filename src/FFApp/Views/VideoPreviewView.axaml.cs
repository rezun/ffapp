using Avalonia;
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

        TrimInTextBox.LostFocus += OnTrimInLostFocus;
        TrimInTextBox.KeyDown += OnTrimTextBoxKeyDown;
        TrimOutTextBox.LostFocus += OnTrimOutLostFocus;
        TrimOutTextBox.KeyDown += OnTrimTextBoxKeyDown;
    }

    /// <summary>
    /// Height the preview needs to show its header, controls and the minimum video
    /// surface without overlap, regardless of the height it is currently given.
    /// </summary>
    public double NaturalHeight { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        // DesiredSize is clamped to the available height, so measure once without a
        // height limit to learn the natural height, then measure for real.
        NaturalHeight = base.MeasureOverride(availableSize.WithHeight(double.PositiveInfinity)).Height;
        return base.MeasureOverride(availableSize);
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

    private void OnTrimInLostFocus(object? sender, RoutedEventArgs e)
    {
        _vm?.ApplyTrimInText(TrimInTextBox.Text ?? "");
    }

    private void OnTrimOutLostFocus(object? sender, RoutedEventArgs e)
    {
        _vm?.ApplyTrimOutText(TrimOutTextBox.Text ?? "");
    }

    private void OnTrimTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox tb)
        {
            if (tb == TrimInTextBox)
                _vm?.ApplyTrimInText(tb.Text ?? "");
            else if (tb == TrimOutTextBox)
                _vm?.ApplyTrimOutText(tb.Text ?? "");

            // Move focus away to indicate acceptance
            TimelineSlider.Focus();
            e.Handled = true;
        }
    }
}
