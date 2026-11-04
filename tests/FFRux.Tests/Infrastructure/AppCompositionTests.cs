using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFRux.Core;
using FFRux.Infrastructure;
using FFRux.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace FFRux.Tests.Infrastructure;

public sealed class AppCompositionTests
{
    [Fact]
    public void AppDataDirectory_IsSeparateFromFFApp() =>
        FFRuxPaths.AppDataDirectory().Should().NotBe(AppPaths.AppDataDirectory(new AppIdentity("FFApp", "ffapp")));

    [Fact]
    public void ConfigureServices_ResolvesMainWindowAndSharedServices()
    {
        // The window is only needed once a dialog opens; resolving the graph must not touch it.
        using var provider = App.ConfigureServices(() => throw new InvalidOperationException("No window in tests."));

        provider.GetRequiredService<MainWindowViewModel>().Should().NotBeNull();
        provider.GetRequiredService<IProcessRunner>().Should().NotBeNull();
        provider.GetRequiredService<IMediaProbeService>().Should().NotBeNull();
        provider.GetRequiredService<IRemuxJobRunner>().Should().NotBeNull();
    }
}
