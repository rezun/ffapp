using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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

            var services = new ServiceCollection();
            services.AddSingleton<IDependencyService, DependencyManager>();
            services.AddSingleton<ISettingsService, SettingsService>();
            services.AddSingleton<IPresetService, PresetService>();
            services.AddSingleton<IValidationService, ValidationService>();
            services.AddSingleton<IProcessRunner, ProcessRunner>();
            services.AddSingleton<IDialogService>(new DialogService(() => mainWindow));

            var provider = services.BuildServiceProvider();

            var vm = new MainWindowViewModel(provider);
            mainWindow.DataContext = vm;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
