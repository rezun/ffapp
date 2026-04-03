using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Infrastructure;
using FFApp.Services;
using FFApp.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace FFApp;

public partial class App : Application
{
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
            services.AddSingleton<ExecutablePathsConfig>();
            services.AddSingleton<IDependencyService, DependencyManager>();
            services.AddSingleton<ISettingsService>(_ => new SettingsService());
            services.AddSingleton<IPresetService>(_ => new PresetService());
            services.AddSingleton<IValidationService, ValidationService>();
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<IDialogService>(new DialogService(() => mainWindow));
            services.AddSingleton<ICommandBuilder, DefaultCommandBuilder>();
            services.AddSingleton<IMediaProbeService, MediaProbeService>();
            services.AddSingleton<IJobExecutionService, DefaultJobExecutionService>();
            services.AddSingleton<Func<QueuedJob, IJobExecutionService>>(sp =>
                _ => sp.GetRequiredService<IJobExecutionService>());
            services.AddSingleton<IQueueService, QueueService>();
            services.AddSingleton<IMessenger>(WeakReferenceMessenger.Default);

            var provider = services.BuildServiceProvider();

            var vm = new MainWindowViewModel(provider);
            mainWindow.DataContext = vm;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
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
