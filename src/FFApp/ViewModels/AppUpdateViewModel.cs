using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Services;
using Microsoft.Extensions.Logging;

namespace FFApp.ViewModels;

/// <summary>
/// Presents update progress and actions in the main window status bar.
/// </summary>
public sealed partial class AppUpdateViewModel(
    IAppUpdateSource source, IUpdateHost host, IDialogService dialogs,
    ILogger<AppUpdateViewModel> logger) : ObservableObject
{
    private AppUpdate? _update;
    private JobListPanelViewModel? _jobList;
    private CancellationTokenSource? _checkCancellation;
    private Task? _cancelCheckTask;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand), nameof(RestartNowCommand))]
    private bool _isBusy;

    [ObservableProperty] private bool _canRestart;
    [ObservableProperty] private bool _canOpenDownloads;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    private int? _downloadPercentage;

    public bool IsDownloading => DownloadPercentage.HasValue;
    public bool IsVisible => !IsQueueRunning && _checkCancellation?.IsCancellationRequested != true
        && StatusText.Length > 0;
    private bool IsQueueRunning => _jobList?.IsRunning == true;
    private bool IsRestarting => IsBusy && _checkCancellation is null;
    private bool CanCheckForUpdates => !IsBusy && !IsQueueRunning;
    private bool CanRestartNow => CanCheckForUpdates && CanRestart;

    internal void AttachJobList(JobListPanelViewModel? jobList)
    {
        if (_jobList is not null)
        {
            _jobList.PropertyChanged -= OnJobListPropertyChanged;
            _jobList.IsStartBlockedByUpdate = false;
        }

        _jobList = jobList;
        if (_jobList is not null)
        {
            _jobList.PropertyChanged += OnJobListPropertyChanged;
            _jobList.IsStartBlockedByUpdate = IsRestarting;
        }
        RefreshQueueState();
    }

    private void OnJobListPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(JobListPanelViewModel.IsRunning))
            RefreshQueueState();
    }

    private void RefreshQueueState()
    {
        // Let the queue start without waiting for update cancellation callbacks or download cleanup.
        if (IsQueueRunning && _checkCancellation is { } cancellation)
            _cancelCheckTask ??= cancellation.CancelAsync();

        OnPropertyChanged(nameof(IsVisible));
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        RestartNowCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        if (_jobList is not null)
            _jobList.IsStartBlockedByUpdate = IsRestarting;
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesAsync() => CheckAsync(manual: true);

    public async Task CheckAsync(bool manual, CancellationToken cancellationToken = default)
    {
        if (!CanCheckForUpdates) return;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _checkCancellation = cancellation;
        var token = cancellation.Token;
        IsBusy = true;
        StatusText = "Checking for updates…";
        var result = "";
        var acceptingProgress = false;
        try
        {
            var update = await source.CheckAsync(token);
            token.ThrowIfCancellationRequested();
            if (update is { CanInstall: true, IsDownloaded: false })
            {
                StatusText = $"Downloading version {update.Version}…";
                DownloadPercentage = 0;
                acceptingProgress = true;
                var progress = new Progress<int>(percentage =>
                {
                    if (acceptingProgress && !token.IsCancellationRequested)
                        DownloadPercentage = percentage;
                });
                await source.DownloadAsync(progress, token);
                update = update with { IsDownloaded = true };
            }
            _update = update;
            if (update is null && manual)
                result = "FFApp and FFRux are up to date.";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // UI operation boundary: third-party update/network failures must not stop startup.
            // A failed download is not offered for installation; the next check retries it.
            logger.LogWarning(ex, "Checking for or downloading updates failed");
            if (manual && !token.IsCancellationRequested)
                result = acceptingProgress
                    ? "Could not download the update. Try again from the Info menu."
                    : "Could not check for updates. Try again from the Info menu.";
        }
        finally
        {
            acceptingProgress = false;
            if (_cancelCheckTask is { } cancelTask)
            {
                try { await cancelTask; }
                catch (AggregateException ex) { logger.LogWarning(ex, "Canceling the update operation failed"); }
                _cancelCheckTask = null;
            }
            _checkCancellation = null;
            DownloadPercentage = null;
            ShowResult(token.IsCancellationRequested ? "" : result);
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRestartNow))]
    private async Task RestartNowAsync()
    {
        if (!CanRestartNow) return;
        IsBusy = true;
        StatusText = "Preparing to restart…";
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
        finally
        {
            ShowResult();
            IsBusy = false;
        }
    }

    [RelayCommand]
    private Task OpenDownloadsAsync() => host.OpenDownloadsAsync();

    [RelayCommand]
    private void Dismiss() => StatusText = "";

    private void ShowResult(string message = "")
    {
        CanRestart = _update is { CanInstall: true, IsDownloaded: true };
        CanOpenDownloads = _update is { CanInstall: false };
        StatusText = _update switch
        {
            { CanInstall: true, IsDownloaded: true } => $"Version {_update.Version} is ready. Restart FFApp to update.",
            { CanInstall: false } => $"Version {_update.Version} is available.",
            _ => message,
        };
    }
}
