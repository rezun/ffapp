using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using FFRux.ViewModels;

namespace FFRux.Views;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // async void: Avalonia lifecycle override — it must return void. Failures are caught and logged.
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || DataContext is not MainWindowViewModel { IsRunning: true } vm)
            return;

        e.Cancel = true;
        try
        {
            if (!await vm.PrepareToCloseAsync())
                return;
            _closeConfirmed = true;
            Close();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Closing FFRux failed: {ex}");
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var canAdd = DataContext is MainWindowViewModel { IsEditable: true } && e.DataTransfer.TryGetFiles() is not null;
        e.DragEffects = canAdd ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    // async void: Avalonia event handler bridge — the drop event cannot await. Failures are caught and logged.
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || e.DataTransfer.TryGetFiles() is not { } files)
            return;

        var paths = files.Select(file => file.TryGetLocalPath()).OfType<string>().ToList();
        try
        {
            await vm.OpenFilesAsync(paths);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Adding dropped files failed: {ex}");
        }
    }
}
