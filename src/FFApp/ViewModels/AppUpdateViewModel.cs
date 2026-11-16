using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Services;
using Microsoft.Extensions.Logging;

namespace FFApp.ViewModels;

public sealed partial class AppUpdateViewModel(
    IAppUpdateSource source, IUpdateHost host, IDialogService dialogs,
    ILogger<AppUpdateViewModel> logger) : ObservableObject
{
    private AppUpdate? _update;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _canDownload;
    [ObservableProperty] private bool _canRestart;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string _downloadLabel = "Download update";
    [ObservableProperty] private int _downloadProgress;
    [ObservableProperty] private bool _isDownloading;

    [RelayCommand]
    private Task CheckForUpdatesAsync() => CheckAsync(manual: true);

    public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _update = await source.CheckAsync(cancellationToken);
            Refresh();
            if (_update is null && manual)
                await dialogs.ShowMessageAsync("No updates available", "You are using the latest available version of FFApp and FFRux.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // UI operation boundary: third-party update/network failures must not stop startup.
            logger.LogWarning(ex, "Checking for updates failed");
            if (manual)
                await dialogs.ShowErrorAsync("Could not check for updates", "Check your internet connection and try again.\n\n" + ex.Message);
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task DownloadUpdateAsync()
    {
        if (IsBusy || _update is null || !CanDownload) return;
        IsBusy = true;
        try
        {
            if (!_update.CanInstall)
            {
                await host.OpenDownloadsAsync();
                return;
            }
            IsDownloading = true;
            DownloadProgress = 0;
            StatusText = $"Downloading FFApp and FFRux {_update.Version}…";
            var progress = new Progress<int>(value => DownloadProgress = value);
            await source.DownloadAsync(value => ((IProgress<int>)progress).Report(value));
            _update = _update with { IsDownloaded = true };
        }
        catch (Exception ex)
        {
            // UI command boundary: preserve the update offer so a failed download can be retried.
            logger.LogError(ex, "Downloading update failed");
            await dialogs.ShowErrorAsync("Could not download update", "The update was not installed. Please try again.\n\n" + ex.Message);
        }
        finally
        {
            IsDownloading = false;
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task RestartAndInstallAsync()
    {
        if (IsBusy || !CanRestart) return;
        IsBusy = true;
        try
        {
            if (await dialogs.ConfirmAsync("Install update", "Restart FFApp to install the update for FFApp and FFRux? Finish your work and close FFRux first."))
                await host.RestartAsync(source.ApplyAndRestart);
        }
        catch (Exception ex)
        {
            // UI command boundary: keep the downloaded update available after a failed restart.
            logger.LogError(ex, "Installing update failed");
            await dialogs.ShowErrorAsync("Could not install update", ex.Message);
        }
        finally { IsBusy = false; }
    }

    private void Refresh()
    {
        IsVisible = _update is not null;
        CanDownload = _update is { IsDownloaded: false };
        CanRestart = _update is { CanInstall: true, IsDownloaded: true };
        DownloadLabel = _update is { CanInstall: false } ? "Open download page" : "Download update";
        StatusText = _update switch
        {
            { IsDownloaded: true } => $"FFApp and FFRux {_update.Version} are ready to install.",
            { CanInstall: false } => $"FFApp and FFRux {_update.Version} are available. Install the release package to enable in-app updates.",
            not null => $"FFApp and FFRux {_update.Version} are available.",
            _ => "",
        };
    }
}
