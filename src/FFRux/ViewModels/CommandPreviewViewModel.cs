using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFRux.Services;

namespace FFRux.ViewModels;

/// <summary>The collapsible card under the track table: the generated commands and the log of the last job.</summary>
public sealed partial class CommandPreviewViewModel(IDialogService dialogs) : ObservableObject
{
    private readonly StringBuilder _log = new();

    [ObservableProperty] private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCommand), nameof(VisibleText))]
    private bool _showLog;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleText))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private string _commandText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleText))]
    [NotifyCanExecuteChangedFor(nameof(CopyCommand))]
    private string _logText = "";

    public bool ShowCommand => !ShowLog;

    public string VisibleText => ShowLog ? LogText : CommandText;

    public string ToggleText => IsExpanded ? "▾ Hide" : "▸ Show";

    public void ClearLog()
    {
        _log.Clear();
        LogText = "";
    }

    public void AppendLog(string line)
    {
        _log.AppendLine(line);
        LogText = _log.ToString();
    }

    public void ShowLogExpanded()
    {
        ShowLog = true;
        IsExpanded = true;
    }

    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(ToggleText));

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void SelectCommand()
    {
        ShowLog = false;
        IsExpanded = true;
    }

    [RelayCommand]
    private void SelectLog() => ShowLogExpanded();

    [RelayCommand(CanExecute = nameof(CanCopy))]
    private Task CopyAsync() => dialogs.SetClipboardTextAsync(VisibleText);

    private bool CanCopy() => VisibleText.Length > 0;
}
