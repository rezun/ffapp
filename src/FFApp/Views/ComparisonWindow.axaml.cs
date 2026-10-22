using System.Diagnostics;
using System.ComponentModel;
using Avalonia.Controls;
using FFApp.ViewModels;

namespace FFApp.Views;

public sealed partial class ComparisonWindow : Window
{
    private bool _saved;
    private bool _closing;
    private ComparisonViewModel? _vm;
    private double _resultsHeight = 250;
    public ComparisonWindow()
    {
        InitializeComponent();
        DataContextChanged += OnContextChanged;
        Closed += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnWorkflowChanged;
        };
        Opened += (_, _) => (DataContext as ComparisonViewModel)?.PreviewSelection();
        SampleTimeline.SampleSelected += (_, id) =>
        {
            if (DataContext is ComparisonViewModel vm)
                vm.SelectedSample = vm.Samples.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));
        };
    }

    private void OnCloseWindow(object? sender, EventArgs e) => Close();

    private void OnContextChanged(object? sender, EventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnWorkflowChanged;
        _vm = DataContext as ComparisonViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnWorkflowChanged;
        UpdateResultsLayout();
    }

    private void OnWorkflowChanged(object? sender, PropertyChangedEventArgs e)
    {
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
            Debug.WriteLine($"Closing comparison failed: {ex}");
            vm.Status = $"Could not close comparison: {ex.Message}";
        }
        finally { _closing = false; }
        return false;
    }
}
