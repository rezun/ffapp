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

    private MainWindowViewModel CreateSut() => new(
        _settings,
        _dependencies,
        _config,
        _messenger,
        () => null!,
        _scopeFactory);

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
