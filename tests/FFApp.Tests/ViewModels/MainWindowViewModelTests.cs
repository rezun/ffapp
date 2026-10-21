using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;
using FFApp.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IDependencyService _dependencies = Substitute.For<IDependencyService>();
    private readonly ExecutablePathsConfig _config = new();
    private readonly IMessenger _messenger = Substitute.For<IMessenger>();
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IFfmpegCapabilityService _capabilityService = Substitute.For<IFfmpegCapabilityService>();

    private readonly TranscodingPreferences _preferences = new();

    private MainWindowViewModel CreateSut() => new(
        _settings,
        _dependencies,
        _config,
        _messenger,
        () => null!,
        _scopeFactory,
        _capabilityService,
        _preferences);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_RestoresPriorityPreference(bool enabled)
    {
        _settings.LoadAsync().Returns(new AppSettings { UseLowPriorityTranscoding = enabled });
        _dependencies.DiscoverAsync(Arg.Any<AppSettings>()).Returns(new DependencyStatus(
            new ExecutableValidation("ffmpeg", null, false),
            new ExecutableValidation("ffprobe", null, false),
            new ExecutableValidation("mkvpropedit", null, false)));
        var sut = CreateSut();

        await sut.InitializeAsync();

        sut.UseLowPriorityTranscoding.Should().Be(enabled);
        _preferences.UseLowPriority.Should().Be(enabled);
    }

    [Fact]
    public void ChangingPriority_UpdatesRuntimePreferenceAndSavedSettings()
    {
        var sut = CreateSut();
        sut.UseLowPriorityTranscoding.Should().BeFalse();

        sut.UseLowPriorityTranscoding = true;

        _preferences.UseLowPriority.Should().BeTrue();
        sut.LoadedSettings.Should().NotBeNull();
        sut.LoadedSettings?.UseLowPriorityTranscoding.Should().BeTrue();

        sut.UseLowPriorityTranscoding = false;

        _preferences.UseLowPriority.Should().BeFalse();
        sut.LoadedSettings?.UseLowPriorityTranscoding.Should().BeFalse();
    }

    [Fact]
    public async Task OpenFilesAsync_NullPaths_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.OpenFilesAsync(null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task OpenFilesAsync_EmptyPaths_DoesNotQueue()
    {
        var sut = CreateSut();

        await sut.OpenFilesAsync(Array.Empty<string>());

        sut.SnapshotPendingFiles().Should().BeEmpty();
    }

    [Fact]
    public async Task OpenFilesAsync_WithoutWorkspace_QueuesFiles()
    {
        var sut = CreateSut();

        await sut.OpenFilesAsync(new[] { "/tmp/a.mp4", "/tmp/b.mkv" });

        sut.SnapshotPendingFiles().Should().Equal("/tmp/a.mp4", "/tmp/b.mkv");
    }

    [Fact]
    public async Task OpenFilesAsync_MultipleCallsWithoutWorkspace_Accumulates()
    {
        var sut = CreateSut();

        await sut.OpenFilesAsync(new[] { "/tmp/a.mp4" });
        await sut.OpenFilesAsync(new[] { "/tmp/b.mkv", "/tmp/c.mov" });

        sut.SnapshotPendingFiles()
            .Should().Equal("/tmp/a.mp4", "/tmp/b.mkv", "/tmp/c.mov");
    }
}
