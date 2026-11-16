using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public sealed class DependencySetupViewModelTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Save_RequiresBothToolsButAllowsMissingOptionalComponents(bool ready, bool optionalAvailable)
    {
        var dependencies = Substitute.For<IDependencyService>();
        var settings = Substitute.For<ISettingsService>();
        settings.LoadAsync().Returns(new AppSettings { ComparisonCacheLimitGiB = 12 });
        var runtime = Substitute.For<IComparisonRuntimeService>();
        runtime.Check(Arg.Any<string>()).Returns(new ComparisonRuntimeStatus(false, false, "Comparison libraries missing"));
        var messenger = new WeakReferenceMessenger();
        DependencyStatus? resolved = null;
        var recipient = new object();
        messenger.Register<DependenciesResolvedMessage>(recipient, (_, message) => resolved = message.Value);
        var status = new DependencyStatus(new("ffmpeg", "/tools/ffmpeg", true),
            new("ffprobe", ready ? "/tools/ffprobe" : null, ready, Error: ready ? null : "ffprobe missing"),
            new("mkvpropedit", null, optionalAvailable));
        dependencies.ValidatePathsAsync(Arg.Any<ExecutablePaths>()).Returns(status);
        var sut = new DependencySetupViewModel(dependencies, settings, Substitute.For<IDialogService>(), messenger, runtime);
        await sut.ValidateAndSaveCommand.ExecuteAsync(null);
        if (ready)
        {
            await settings.Received(1).SaveAsync(Arg.Is<AppSettings>(saved => saved.ExecutablePaths.Ffmpeg == "/tools/ffmpeg"
                && saved.ComparisonCacheLimitGiB == 12));
            resolved.Should().Be(status);
            sut.ComparisonStatus.Detail.Should().Contain("missing");
        }
        else
        {
            await settings.DidNotReceive().SaveAsync(Arg.Any<AppSettings>());
            resolved.Should().BeNull();
            sut.StatusText.Should().Contain("ffprobe missing");
        }
    }
}
