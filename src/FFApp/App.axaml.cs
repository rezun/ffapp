using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Infrastructure;
using FFApp.Infrastructure.SingleInstance;
using FFApp.Services;
using LibVLCSharp.Shared;
using FFApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FFApp;

public partial class App : Application
{
    private readonly SingleInstanceCoordinator? _singleInstance;
    private MainWindowViewModel? _mainViewModel;

    public App() { }

    public App(SingleInstanceCoordinator singleInstance)
    {
        ArgumentNullException.ThrowIfNull(singleInstance);
        _singleInstance = singleInstance;
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = new Views.MainWindow();

            var geo = SettingsService.LoadWindowGeometrySync();
            if (geo.Width is { } w && geo.Height is { } h && w > 0 && h > 0)
            {
                mainWindow.Width = w;
                mainWindow.Height = h;
            }
            if (geo.X is { } x && geo.Y is { } y)
            {
                var savedRect = new PixelRect(x, y, (int)mainWindow.Width, (int)mainWindow.Height);
                foreach (var screen in mainWindow.Screens.All)
                {
                    if (screen.Bounds.Intersects(savedRect))
                    {
                        mainWindow.Position = new PixelPoint(x, y);
                        break;
                    }
                }
            }
            if (geo.IsMaximized)
                mainWindow.WindowState = WindowState.Maximized;

            var services = new ServiceCollection();

            services.AddSingleton(TimeProvider.System);

            // Infrastructure services
            services.AddSingleton<ExecutablePathsConfig>();
            services.AddSingleton<TranscodingPreferences>();
            services.AddSingleton<IDependencyService, DependencyManager>();
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IPresetService, PresetService>();
            services.AddSingleton<IValidationService, ValidationService>();
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<IDialogService>(_ => new DialogService(() => mainWindow));
            services.AddSingleton<ICommandBuilder, DefaultCommandBuilder>();
            services.AddSingleton<IMatroskaPostProcessor, MatroskaPostProcessor>();
            services.AddSingleton<IMediaProbeService, MediaProbeService>();
            services.AddSingleton<ICropDetectionService, CropDetectionService>();
            services.AddSingleton<IJobExecutionService, DefaultJobExecutionService>();
            services.AddSingleton<IFfmpegCapabilityService, FfmpegCapabilityService>();
            services.AddSingleton<Func<QueuedJob, IJobExecutionService>>(sp =>
                _ => sp.GetRequiredService<IJobExecutionService>());
            services.AddSingleton<IQueueService, QueueService>();
            services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

            var libVlc = VlcLocator.TryCreate();
            if (libVlc is not null)
                services.AddSingleton(libVlc);

            // ViewModels
            services.AddSingleton<MainWindowViewModel>();
            services.AddTransient<DependencySetupViewModel>();

            // Workspace VMs use Scoped lifetime — all children within one workspace
            // share the same instances, enabling sibling VM injection.
            services.AddScoped<WorkspaceViewModel>();
            services.AddScoped<SourceListPanelViewModel>();
            services.AddScoped<OutputPathViewModel>();
            services.AddScoped<OutputSettingsViewModel>();
            services.AddScoped<PresetViewModel>();
            services.AddScoped<CommandPreviewViewModel>();
            services.AddScoped<JobListPanelViewModel>();
            services.AddScoped<VideoPreviewViewModel>(sp =>
                new VideoPreviewViewModel(
                    sp.GetService<LibVLC>(),
                    sp.GetRequiredService<ICropDetectionService>()));

            // Factory delegate for on-demand DependencySetupViewModel creation
            services.AddSingleton<Func<DependencySetupViewModel>>(sp =>
                () => sp.GetRequiredService<DependencySetupViewModel>());

            var provider = services.BuildServiceProvider();

            var vm = provider.GetRequiredService<MainWindowViewModel>();
            _mainViewModel = vm;
            mainWindow.DataContext = vm;
            desktop.MainWindow = mainWindow;

            // Platform split:
            //  - macOS: file opens always arrive via Apple Events (IActivatableLifetime), never argv.
            //    Reading desktop.Args there would ingest whatever the launcher happens to put in
            //    argv, which is not ours to interpret.
            //  - Windows/Linux: argv is how installers and shells deliver an opened file path.
            if (!OperatingSystem.IsMacOS())
                QueueFilesFromArgs(desktop.Args);

            if (this.TryGetFeature(typeof(IActivatableLifetime)) is IActivatableLifetime activatable)
                activatable.Activated += OnActivatableLifetimeActivated;

            if (_singleInstance is not null)
            {
                _singleInstance.ActivationRequested += OnSingleInstanceActivation;
                desktop.Exit += OnDesktopExit;
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void QueueFilesFromArgs(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0 || _mainViewModel is null)
            return;

        var paths = FilterExistingPaths(args);
        if (paths.Count == 0)
            return;

        // Fire-and-forget: runs during OnFrameworkInitializationCompleted which is synchronous;
        // OpenFilesAsync internally queues paths until the workspace is ready.
        _ = SafeOpenFilesAsync(_mainViewModel, paths);
    }

    private static async Task SafeOpenFilesAsync(MainWindowViewModel vm, IReadOnlyList<string> paths)
    {
        try
        {
            await vm.OpenFilesAsync(paths);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Opening files failed: {ex.Message}");
        }
    }

    // On macOS, NSApplication inspects argv at launch and dispatches anything that looks like
    // a path to application:openFiles: automatically (legacy Cocoa behavior). When the process
    // is started via `dotnet FFApp.dll` — which is what `dotnet run` produces — that puts the
    // managed assembly path on argv, and it arrives in FileActivatedEventArgs alongside any
    // real user file. Runtime files inside our own directory are never legitimate user input,
    // so we reject them regardless of which channel they arrived on.
    private static readonly string RuntimeDirectory =
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(AppContext.BaseDirectory))
        + Path.DirectorySeparatorChar;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    internal static bool IsInsideRuntimeDirectory(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(RuntimeDirectory, PathComparison);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static IReadOnlyList<string> FilterExistingPaths(IReadOnlyList<string> candidates)
    {
        var paths = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                continue;
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
                continue;
            if (IsInsideRuntimeDirectory(candidate))
                continue;
            paths.Add(candidate);
        }
        return paths;
    }

    private void OnActivatableLifetimeActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs fileArgs || _mainViewModel is null)
            return;

        var candidates = new List<string>(fileArgs.Files.Count);
        foreach (var item in fileArgs.Files)
        {
            if (item.TryGetLocalPath() is { Length: > 0 } local)
                candidates.Add(local);
        }

        var paths = FilterExistingPaths(candidates);
        if (paths.Count == 0)
            return;

        var vm = _mainViewModel;
        Dispatcher.UIThread.Post(() => _ = SafeOpenFilesAsync(vm, paths));
    }

    private void OnSingleInstanceActivation(object? sender, SingleInstanceActivationEventArgs e)
    {
        var paths = FilterExistingPaths(e.Args);
        Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } window)
                return;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();

            if (paths.Count > 0 && _mainViewModel is { } vm)
                _ = SafeOpenFilesAsync(vm, paths);
        });
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        // Event handler bridge: lifetime.Exit is synchronous, so fire-and-forget disposal
        // is unavoidable here. Swallow exceptions — shutdown is terminal either way.
        _ = DisposeSingleInstanceAsync();
    }

    private async Task DisposeSingleInstanceAsync()
    {
        if (_singleInstance is null)
            return;
        try
        {
            await _singleInstance.DisposeAsync();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to dispose single-instance coordinator: {ex.Message}");
        }
    }

    public void SetThemeVariant(ThemePreference preference)
    {
        RequestedThemeVariant = preference switch
        {
            ThemePreference.Dark => ThemeVariant.Dark,
            ThemePreference.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }
}
