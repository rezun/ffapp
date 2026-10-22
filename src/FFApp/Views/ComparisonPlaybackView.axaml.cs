using System.ComponentModel;
using Avalonia.Controls;
using FFApp.ViewModels;

namespace FFApp.Views;

public sealed partial class ComparisonPlaybackView : UserControl
{
    private ComparisonPlaybackViewModel? _vm;
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
    }
    private void UpdateLayoutMode()
    {
        LeftPane.IsVisible = _vm?.IsSourcePreview == true || _vm?.Layout != "B only";
        RightPane.IsVisible = _vm?.IsSourcePreview != true && _vm?.Layout != "A only";
        VideoGrid.ColumnDefinitions[0].Width = LeftPane.IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        VideoGrid.ColumnDefinitions[1].Width = RightPane.IsVisible ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }
}
