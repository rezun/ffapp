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

}
