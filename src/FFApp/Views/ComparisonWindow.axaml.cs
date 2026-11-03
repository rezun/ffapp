using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FFApp.Infrastructure;
using FFApp.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFApp.Views;

public sealed partial class ComparisonWindow : Window
{
    private bool _saved;
    private bool _closing;
    private ComparisonViewModel? _vm;
    private double _resultsHeight = 250;

    /// <summary>Set by whoever opens the window; windows are created by Avalonia's loader, not by DI.</summary>
    public ILogger Logger { get; init; } = NullLogger<ComparisonWindow>.Instance;
    public ComparisonWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPlaybackKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPlaybackKeyUp, RoutingStrategies.Tunnel);
        DataContextChanged += OnContextChanged;
        Closed += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnWorkflowChanged;
        };
        Opened += (_, _) =>
        {
            // Frames are sRGB; without a tagged layer macOS shows them oversaturated on P3 displays.
            MacOSColorSpace.TagSrgbWhenRendered(this);
            (DataContext as ComparisonViewModel)?.PreviewSelection();
        };
        SampleTimeline.SampleSelected += (_, id) =>
        {
            if (DataContext is ComparisonViewModel vm)
                vm.SelectedSample = vm.Samples.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        };
    }

    private void OnCloseWindow(object? sender, EventArgs e) => Close();

    private bool IsPlaybackShortcut(KeyEventArgs e) =>
        e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None
        && SamplePlayback.IsEffectivelyVisible && _vm?.Playback.HasMedia == true && !IsEditingText(e);

    private bool IsEditingText(KeyEventArgs e) =>
        e.Source is Control control && (control is TextBox || control.GetVisualAncestors().OfType<TextBox>().Any());

    private void OnPlaybackKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsPlaybackShortcut(e))
        {
            e.Handled = true;
            _vm?.Playback.TogglePlayCommand.Execute(null);
            return;
        }
        if (_vm is null || e.KeyModifiers != KeyModifiers.None || IsEditingText(e)) return;
        if (e.Key == Key.I && SampleEditor.IsEffectivelyVisible && _vm.SetStartToPlayheadCommand.CanExecute(null))
        {
            e.Handled = true;
            _vm.SetStartToPlayheadCommand.Execute(null);
        }
        else if (e.Key == Key.F && SamplePlayback.IsEffectivelyVisible)
        {
            e.Handled = true;
            _vm.TogglePreviewSizeCommand.Execute(null);
        }
    }

    private void OnPlaybackKeyUp(object? sender, KeyEventArgs e)
    {
        // Buttons can activate on release, so consume both halves of the shortcut.
        if (IsPlaybackShortcut(e)) e.Handled = true;
    }

    private void OnContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnWorkflowChanged;
        _vm = DataContext as ComparisonViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnWorkflowChanged;
        UpdateResultsLayout();
    }

    private void OnWorkflowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ComparisonViewModel.IsDeleted) && _vm?.IsDeleted == true)
        {
            _saved = true;
            Close();
            return;
        }
        if (e.PropertyName == nameof(ComparisonViewModel.ShowResultTable)) UpdateResultsLayout();
    }

    private void UpdateResultsLayout()
    {
        var row = CompareGrid.RowDefinitions[3];
        if (_vm?.ShowResultTable == true)
        {
            if (row.Height.Value == 0) row.Height = new GridLength(_resultsHeight);
        }
        else
        {
            if (row.Height.Value > 0) _resultsHeight = row.Height.Value;
            row.Height = new GridLength(0);
        }
    }

    // Avalonia's synchronous closing callback cannot return a Task. Await cancellation
    // and persistence before allowing the native window and its DI scope to close.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_saved || DataContext is not ComparisonViewModel) { base.OnClosing(e); return; }
        e.Cancel = true;
        base.OnClosing(e);
        await CloseSavedAsync();
    }

    public async Task<bool> CloseSavedAsync()
    {
        if (_saved) return true;
        if (_closing || DataContext is not ComparisonViewModel vm) return false;
        _closing = true;
        try
        {
            if (await vm.CloseAsync())
            {
                _saved = true;
                Close();
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Closing comparison failed");
            vm.Status = $"Could not close comparison: {ex.Message}";
        }
        finally { _closing = false; }
        return false;
    }
}
