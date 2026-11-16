using System.Diagnostics;
using FFApp.ViewModels;
using FFApp.Views;

namespace FFApp.Services;

public sealed class DesktopUpdateHost(MainWindow window, IDialogService dialogs) : IUpdateHost
{
    public async Task OpenDownloadsAsync() => await window.Launcher.LaunchUriAsync(VelopackUpdateSource.DownloadsUri);

    public async Task<bool> RestartAsync(Action applyUpdate)
    {
        // Prevent a queue start or file drop while asynchronous save/close prompts run.
        window.IsEnabled = false;
        try
        {
            if (await IsBlockedAsync()) return false;
            if (!await window.CloseSavedAsync(closeWindow: false)) return false;
            if (await IsBlockedAsync()) return false;
            applyUpdate();
            return true;
        }
        finally
        {
            window.IsEnabled = true;
        }
    }

    private async Task<bool> IsBlockedAsync()
    {
        if (window.DataContext is MainWindowViewModel { CurrentView: WorkspaceViewModel { JobList.IsRunning: true } })
        {
            await dialogs.ShowMessageAsync("Update postponed", "Finish or stop the current processing queue before installing the update.");
            return true;
        }

        var companions = Process.GetProcessesByName("FFRux");
        var isCompanionOpen = companions.Length > 0;
        foreach (var process in companions) process.Dispose();
        if (isCompanionOpen)
            await dialogs.ShowMessageAsync("Close FFRux first", "FFApp and FFRux are updated together. Close FFRux, then choose Restart and install again.");
        return isCompanionOpen;
    }
}
