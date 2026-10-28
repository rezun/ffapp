using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FFRux.ViewModels;

namespace FFRux.Views;

public partial class TracksView : UserControl
{
    private const double DragThreshold = 4;

    private TrackViewModel? _pressedTrack;
    private Point _pressedAt;
    private bool _dragging;

    public TracksView()
    {
        InitializeComponent();
        TrackList.ContainerPrepared += OnContainerPrepared;
        TrackList.SelectionChanged += OnSelectionChanged;
        // Tunnel handlers see the press before the ListBoxItem selects, so selection keeps working.
        TrackList.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
        TrackList.AddHandler(PointerMovedEvent, OnRowMoved, RoutingStrategies.Tunnel);
        TrackList.AddHandler(PointerReleasedEvent, OnRowReleased, RoutingStrategies.Tunnel);
        TrackList.AddHandler(PointerCaptureLostEvent, (_, _) => EndDrag(), RoutingStrategies.Tunnel);
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    // Group header rows are disabled containers so clicks and keyboard selection skip them.
    private static void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e) =>
        e.Container.IsEnabled = e.Container.DataContext is TrackViewModel;

    private void OnSelectionChanged(object? sender, SelectionChangedEventArgs e) =>
        ViewModel?.SelectTracks([.. TrackList.SelectedItems?.OfType<TrackViewModel>() ?? []]);

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(TrackList).Properties.IsLeftButtonPressed || ViewModel is not { IsEditable: true })
            return;
        if (e.Source is Visual visual && visual.FindAncestorOfType<CheckBox>(includeSelf: true) is not null)
            return;
        _pressedTrack = TrackAt(e.GetPosition(TrackList));
        _pressedAt = e.GetPosition(TrackList);
    }

    private void OnRowMoved(object? sender, PointerEventArgs e)
    {
        if (_pressedTrack is not { } dragged || ViewModel is not { } vm)
            return;

        var position = e.GetPosition(TrackList);
        if (!_dragging)
        {
            if (Math.Abs(position.Y - _pressedAt.Y) < DragThreshold)
                return;
            _dragging = true;
            e.Pointer.Capture(TrackList);
        }

        if (TrackAt(position) is { } target && target != dragged && target.Kind == dragged.Kind)
        {
            vm.MoveTrack(dragged, target);
            if (TrackList.SelectedItems?.Contains(dragged) != true)
                TrackList.SelectedItem = dragged;
        }
        e.Handled = true;
    }

    private void OnRowReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
        EndDrag();
    }

    private void EndDrag()
    {
        _pressedTrack = null;
        _dragging = false;
    }

    private TrackViewModel? TrackAt(Point position) =>
        TrackList.InputHitTest(position) is Visual hit
            ? hit.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TrackViewModel
            : null;
}
