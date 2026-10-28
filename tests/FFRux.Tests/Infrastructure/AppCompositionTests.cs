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
    public void Identity_IsSeparateFromFFApp()
    {
        var ffapp = new AppIdentity("FFApp", "ffapp");

        FFRuxPaths.Identity.Should().Be(new AppIdentity("FFRux", "ffrux"));
        FFRuxPaths.Identity.Name.Should().NotBe(ffapp.Name);
        FFRuxPaths.AppDataDirectory().Should().NotBe(AppPaths.AppDataDirectory(ffapp));
    }

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
