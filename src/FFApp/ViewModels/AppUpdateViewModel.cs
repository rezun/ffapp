using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Services;
using Microsoft.Extensions.Logging;

namespace FFApp.ViewModels;

/// <summary>
/// Checks for updates and downloads them in the background. Once an update is ready, a hint tells the
/// user that restarting FFApp installs it; the next launch applies it as well.
/// </summary>
public sealed partial class AppUpdateViewModel(
    IAppUpdateSource source, IUpdateHost host, IDialogService dialogs,
    ILogger<AppUpdateViewModel> logger) : ObservableObject
{
    private AppUpdate? _update;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isVisible;
    [ObservableProperty] private bool _isDismissed;
    [ObservableProperty] private bool _canRestart;
    [ObservableProperty] private bool _canOpenDownloads;
    [ObservableProperty] private string _statusText = "";

    [RelayCommand]
    private Task CheckForUpdatesAsync() => CheckAsync(manual: true);

    public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            _update = await source.CheckAsync(cancellationToken);
            if (_update is { CanInstall: true, IsDownloaded: false })
            {
                await source.DownloadAsync(cancellationToken);
                _update = _update with { IsDownloaded = true };
            }
            if (manual) IsDismissed = false;
            if (_update is null && manual)
                await dialogs.ShowMessageAsync("No updates available", "You are using the latest available version of FFApp and FFRux.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // UI operation boundary: third-party update/network failures must not stop startup.
            // A failed download is not offered for installation; the next check retries it.
            logger.LogWarning(ex, "Checking for or downloading updates failed");
            if (manual)
                await dialogs.ShowErrorAsync("Could not update", "Check your internet connection and try again.\n\n" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task RestartNowAsync()
    {
        if (IsBusy || !CanRestart) return;
        IsBusy = true;
        try
        {
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

    [RelayCommand]
    private Task OpenDownloadsAsync() => host.OpenDownloadsAsync();

    [RelayCommand]
    private void Dismiss() => IsDismissed = true;

    partial void OnIsDismissedChanged(bool value) => Refresh();

    private void Refresh()
    {
        CanRestart = _update is { CanInstall: true, IsDownloaded: true };
        CanOpenDownloads = _update is { CanInstall: false };
        IsVisible = !IsDismissed && (CanRestart || CanOpenDownloads);
        StatusText = _update switch
        {
            { CanInstall: true, IsDownloaded: true } => $"Version {_update.Version} is ready. Restart FFApp to update.",
            { CanInstall: false } => $"Version {_update.Version} is available.",
            _ => "",
        };
    }
}
