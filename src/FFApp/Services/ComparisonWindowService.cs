using Avalonia.Controls;
using FFApp.Core.Models;
using FFApp.ViewModels;
using FFApp.Views;
using Microsoft.Extensions.DependencyInjection;

namespace FFApp.Services;

public sealed class ComparisonWindowService(IServiceScopeFactory scopeFactory, Func<Window> owner) : IComparisonWindowService
{
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
        var scope = scopeFactory.CreateScope();
        try
        {
            var vm = scope.ServiceProvider.GetRequiredService<ComparisonViewModel>();
            await vm.LoadAsync(context, apply);
            var window = new ComparisonWindow { DataContext = vm };
            _windows.Add(path, window);
            window.Closed += (_, _) =>
            {
                _windows.Remove(path);
                scope.Dispose();
            };
            window.Show(owner());
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
