using Avalonia.Controls;
using FFApp.Core.Models;
using FFApp.Core;
using FFApp.Core.Services;
using FFApp.ViewModels;
using FFApp.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFApp.Services;

public sealed class ComparisonWindowService(
    IServiceScopeFactory scopeFactory,
    Func<Window> owner,
    ILoggerFactory loggerFactory,
    IComparisonRuntimeService runtime,
    ExecutablePathsConfig paths,
    IDialogService dialogs) : IComparisonWindowService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ComparisonWindowService>();

    private readonly Dictionary<string, ComparisonWindow> _windows = new(StringComparer.Ordinal);

    public async Task<bool> CloseAllAsync()
    {
        foreach (var window in _windows.Values.ToArray())
        {
            if (!await window.CloseSavedAsync())
            {
                window.Activate();
                return false;
            }
        }
        return true;
    }

    public async Task ShowAsync(JobContext context, Func<OutputSettings, bool> apply)
    {
        var path = context.MediaInfo.SourcePath;
        if (_windows.TryGetValue(path, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return;
        }
        var availability = await Task.Run(() => runtime.Check(paths.FfmpegPath));
        if (!availability.IsAvailable)
        {
            await dialogs.ShowMessageAsync("Comparison playback unavailable", availability.Message
                + " Open Settings → Tools and components for installation links and diagnostics.");
            return;
        }
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            var vm = scope.ServiceProvider.GetRequiredService<ComparisonViewModel>();
            await vm.LoadAsync(context, apply);
            var window = new ComparisonWindow
            {
                DataContext = vm,
                Logger = loggerFactory.CreateLogger<ComparisonWindow>(),
            };
            _windows.Add(path, window);
            window.Closed += (_, _) =>
            {
                _windows.Remove(path);
                // Closed is synchronous; track cleanup in an async boundary so
                // scoped decoders can cancel and await their background work.
                _ = DisposeScopeAsync();
            };
            window.Show(owner());

            async Task DisposeScopeAsync()
            {
                try { await scope.DisposeAsync(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Comparison cleanup failed"); }
            }
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }
}
