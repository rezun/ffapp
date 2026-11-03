using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using FFApp.Core;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFApp.Infrastructure.Logging;
using FFApp.Infrastructure.SingleInstance;
using FFRux.Core;
using FFRux.Infrastructure;
using FFRux.Services;
using FFRux.ViewModels;
using FFRux.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FFRux;

public partial class App : Application
{
    private readonly SingleInstanceCoordinator? _singleInstance;
    private readonly Serilog.ILogger? _appLog;
    private ILogger _logger = NullLogger<App>.Instance;
    private MainWindowViewModel? _mainViewModel;

    public App() { }

    public App(SingleInstanceCoordinator singleInstance, Serilog.ILogger appLog)
    {
        ArgumentNullException.ThrowIfNull(singleInstance);
        ArgumentNullException.ThrowIfNull(appLog);
        _singleInstance = singleInstance;
        _appLog = appLog;
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
            var mainWindow = new MainWindow();
            var provider = ConfigureServices(() => mainWindow, _appLog);
            _logger = provider.GetRequiredService<ILogger<App>>();
            mainWindow.Logger = provider.GetRequiredService<ILogger<MainWindow>>();

            var vm = provider.GetRequiredService<MainWindowViewModel>();
            _mainViewModel = vm;
            mainWindow.DataContext = vm;
            desktop.MainWindow = mainWindow;
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            // OnFrameworkInitializationCompleted is synchronous, so tool discovery cannot be awaited here.
            // OpenFilesAsync awaits the same initialization, so files opened at launch are not lost.
            _ = RunSafelyAsync(vm.InitializeAsync, "Tool discovery");

            // macOS delivers opened files only via Apple Events (IActivatableLifetime); argv there is
            // the launcher's, not ours. Windows and Linux shells pass "Open with" files in argv.
            if (!OperatingSystem.IsMacOS() && desktop.Args is { } args)
                OpenFiles(args);

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

    /// <param name="appLog">The file logger; null in tests, where logging goes nowhere.</param>
    internal static ServiceProvider ConfigureServices(Func<Window> mainWindow, Serilog.ILogger? appLog = null)
    {
        var services = new ServiceCollection();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new JsonSerializerOptions { WriteIndented = true });
        services.AddAppLogging(FFRuxPaths.Identity, appLog);

        // Shared media plumbing from FFApp.Media
        services.AddSingleton<ExecutablePathsConfig>();
        services.AddSingleton<TranscodingPreferences>();
        services.AddSingleton<IDependencyService, DependencyManager>();
        services.AddSingleton<IMediaProbeService, MediaProbeService>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();

        // FFRux services
        services.AddSingleton<IDialogService>(_ => new DialogService(mainWindow));
        services.AddSingleton<ISettingsService, SettingsService>();
        services.AddSingleton<RemuxCommandBuilder>();
        services.AddSingleton<IRemuxJobRunner, RemuxJobRunner>();

        // ViewModels
        services.AddSingleton<OutputViewModel>();
        services.AddSingleton<TrackPropertiesViewModel>();
        services.AddSingleton<CommandPreviewViewModel>();
        services.AddSingleton<MainWindowViewModel>();

        return services.BuildServiceProvider();
    }

    private void OpenFiles(IReadOnlyList<string> candidates)
    {
        var paths = LaunchFiles.FilterExistingPaths(candidates);
        if (paths.Count == 0 || _mainViewModel is not { } vm)
            return;

        // Every caller is a synchronous platform callback, so the open runs fire-and-forget.
        _ = RunSafelyAsync(() => vm.OpenFilesAsync(paths), "Opening files");
    }

    private void OnActivatableLifetimeActivated(object? sender, ActivatedEventArgs e)
    {
        if (e is not FileActivatedEventArgs fileArgs)
            return;

        var candidates = new List<string>(fileArgs.Files.Count);
        foreach (var item in fileArgs.Files)
        {
            if (item.TryGetLocalPath() is { Length: > 0 } local)
                candidates.Add(local);
        }

        Dispatcher.UIThread.Post(() => OpenFiles(candidates));
    }

    private void OnSingleInstanceActivation(object? sender, SingleInstanceActivationEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } window)
                return;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;
            window.Show();
            window.Activate();

            OpenFiles(e.Args);
        });
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        // lifetime.Exit is synchronous, so disposal cannot be awaited here.
        if (_singleInstance is { } singleInstance)
            _ = RunSafelyAsync(() => singleInstance.DisposeAsync().AsTask(), "Disposing the single-instance coordinator");
    }

    private async Task RunSafelyAsync(Func<Task> action, string operation)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Operation} failed", operation);
        }
    }
}
