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

    private MainWindowViewModel CreateSut(IComparisonService? comparisons = null) => new(
        _settings,
        _dependencies,
        _config,
        _messenger,
        CreateSetup,
        _scopeFactory,
        _capabilityService,
        _preferences, Substitute.For<ICompanionAppService>(), comparisonService: comparisons);

    private DependencySetupViewModel CreateSetup()
    {
        var runtime = Substitute.For<IComparisonRuntimeService>();
        runtime.Check(Arg.Any<string>()).Returns(new ComparisonRuntimeStatus(false, false, "Optional runtime missing"));
        return new(_dependencies, _settings, Substitute.For<IDialogService>(), _messenger, runtime);
    }

    [Fact]
    public async Task InitializeAsync_UnexpectedFailure_ShowsAnActionableErrorInsteadOfAnEndlessSpinner()
    {
        _settings.LoadAsync().Returns<AppSettings>(_ => throw new IOException("Settings are locked"));
        var sut = CreateSut();
        await sut.InitializeAsync();
        sut.CurrentView.Should().BeOfType<DependencySetupViewModel>().Subject.StatusText.Should().Contain("Settings are locked");
    }

    [Fact]
    public async Task InitializeAsync_PartialDiscovery_PreservesFoundPathAndExplainsTheMissingTool()
    {
        _settings.LoadAsync().Returns(new AppSettings());
        _dependencies.DiscoverAsync(Arg.Any<ExecutablePaths>()).Returns(new DependencyStatus(
            new("ffmpeg", "/tools/ffmpeg", true, "ffmpeg version 9.0.2"),
            new("ffprobe", null, false, Error: "ffprobe was not found."),
            new("mkvpropedit", null, false)));
        var sut = CreateSut();
        await sut.InitializeAsync();
        var setup = sut.CurrentView.Should().BeOfType<DependencySetupViewModel>().Subject;
        setup.FfmpegPath.Should().Be("/tools/ffmpeg");
        setup.FfprobeStatus.Should().Contain("not found");
        setup.StatusText.Should().Contain("ffprobe");
        setup.ComparisonStatus.Should().Be("Optional runtime missing");
    }

    [Fact]
    public async Task InitializeAsync_CacheCleanupFailure_DoesNotBlockDependencyDiscovery()
    {
        var comparisons = Substitute.For<IComparisonService>();
        comparisons.When(c => c.SetCacheLimit(Arg.Any<long>())).Do(_ => throw new IOException("Locked clip"));
        _settings.LoadAsync().Returns(new AppSettings());
        _dependencies.DiscoverAsync(Arg.Any<ExecutablePaths>()).Returns(new DependencyStatus(
            new ExecutableValidation("ffmpeg", null, false),
            new ExecutableValidation("ffprobe", null, false),
            new ExecutableValidation("mkvpropedit", null, false)));
        var sut = CreateSut(comparisons);
        await sut.InitializeAsync();
        await _dependencies.Received(1).DiscoverAsync(Arg.Any<ExecutablePaths>());
        sut.ComparisonStorageStatus.Should().Contain("Locked clip");
    }

    [Fact]
    public async Task InitializeAsync_AppliesPersistedComparisonCacheLimit()
    {
        var comparisons = Substitute.For<IComparisonService>();
        _settings.LoadAsync().Returns(new AppSettings { ComparisonCacheLimitGiB = 12 });
        _dependencies.DiscoverAsync(Arg.Any<ExecutablePaths>()).Returns(new DependencyStatus(
            new ExecutableValidation("ffmpeg", null, false),
            new ExecutableValidation("ffprobe", null, false),
            new ExecutableValidation("mkvpropedit", null, false)));
        var sut = CreateSut(comparisons);
        await sut.InitializeAsync();
        sut.ComparisonCacheLimitGiB.Should().Be(12);
        comparisons.Received(1).SetCacheLimit(12L * 1024 * 1024 * 1024);
    }

    [Fact]
    public async Task ApplyComparisonCacheLimit_PersistsAndAppliesLimit_WithoutLosingOtherPreferences()
    {
        var comparisons = Substitute.For<IComparisonService>();
        comparisons.GetCacheUsage().Returns(new ComparisonCacheUsage(2048, 0, 1024));
        var sut = CreateSut(comparisons);
        sut.UseLowPriorityTranscoding = true;
        sut.ComparisonCacheLimitGiB = 3;
        await sut.ApplyComparisonCacheLimitCommand.ExecuteAsync(null);
        await _settings.Received(1).SaveAsync(Arg.Is<AppSettings>(s =>
            s.ComparisonCacheLimitGiB == 3 && s.UseLowPriorityTranscoding));
        comparisons.Received(1).SetCacheLimit(3L * 1024 * 1024 * 1024);
        sut.LoadedSettings?.ComparisonCacheLimitGiB.Should().Be(3);
        sut.DeleteAllClipsLabel.Should().Contain($"{1d:F1} KiB");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1025)]
    [InlineData(1.5)]
    public async Task ApplyComparisonCacheLimit_RejectsInvalidInput(double limit)
    {
        var comparisons = Substitute.For<IComparisonService>();
        comparisons.GetCacheUsage().Returns(new ComparisonCacheUsage(0, 0, 0));
        var sut = CreateSut(comparisons);
        sut.ComparisonCacheLimitGiB = (decimal)limit;
        await sut.ApplyComparisonCacheLimitCommand.ExecuteAsync(null);
        await _settings.DidNotReceive().SaveAsync(Arg.Any<AppSettings>());
        comparisons.DidNotReceive().SetCacheLimit(Arg.Any<long>());
        sut.ComparisonStorageStatus.Should().Contain("Could not apply cache limit");
    }

    [Fact]
    public void DeleteAllComparisonClips_ReportsFreedSpaceAndRefreshesUsage()
    {
        var comparisons = Substitute.For<IComparisonService>();
        comparisons.GetCacheUsage().Returns(new ComparisonCacheUsage(1024, 0, 1024));
        comparisons.ClearClips().Returns(2048);
        var sut = CreateSut(comparisons);
        sut.DeleteAllComparisonClipsCommand.Execute(null);
        comparisons.Received(1).ClearClips();
        sut.ComparisonStorageStatus.Should().Contain($"{2d:F1} KiB");
        sut.ComparisonStorageUsage.Should().Contain("Protected by open comparisons");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitializeAsync_RestoresPriorityPreference(bool enabled)
    {
        _settings.LoadAsync().Returns(new AppSettings { UseLowPriorityTranscoding = enabled });
        _dependencies.DiscoverAsync(Arg.Any<ExecutablePaths>()).Returns(new DependencyStatus(
            new ExecutableValidation("ffmpeg", null, false),
            new ExecutableValidation("ffprobe", null, false),
            new ExecutableValidation("mkvpropedit", null, false)));
        var sut = CreateSut();

        await sut.InitializeAsync();

        sut.UseLowPriorityTranscoding.Should().Be(enabled);
        _preferences.UseLowPriority.Should().Be(enabled);
    }

    [Fact]
    public async Task OpenFilesAsync_WithoutWorkspace_AccumulatesNonEmptyBatches()
    {
        var sut = CreateSut();

        await sut.OpenFilesAsync([]);
        await sut.OpenFilesAsync(["/tmp/a.mp4"]);
        await sut.OpenFilesAsync(["/tmp/b.mkv", "/tmp/c.mov"]);

        sut.SnapshotPendingFiles().Should().Equal("/tmp/a.mp4", "/tmp/b.mkv", "/tmp/c.mov");
    }
}
