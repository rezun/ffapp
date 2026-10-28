using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFRux.Services;
using FFRux.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFRux.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    private readonly IDependencyService _dependencies = Substitute.For<IDependencyService>();
    private readonly ExecutablePathsConfig _pathsConfig = new();
    private readonly IMediaProbeService _probe = Substitute.For<IMediaProbeService>();
    private readonly IFilePickerService _filePicker = Substitute.For<IFilePickerService>();

    private MainWindowViewModel CreateSut() => new(_dependencies, _pathsConfig, _probe, _filePicker);

    private static DependencyStatus Status(bool ready) => new(
        new ExecutableValidation("ffmpeg", "/opt/bin/ffmpeg", ready),
        new ExecutableValidation("ffprobe", "/opt/bin/ffprobe", ready),
        new ExecutableValidation("mkvpropedit", null, false));

    private void ToolsAvailable(bool ready) =>
        _dependencies.DiscoverAsync(Arg.Any<ExecutablePaths>(), Arg.Any<CancellationToken>())
            .Returns(Status(ready));

    [Fact]
    public async Task InitializeAsync_ConfiguresDiscoveredTools()
    {
        ToolsAvailable(true);
        var sut = CreateSut();

        await sut.InitializeAsync();

        sut.IsReady.Should().BeTrue();
        _pathsConfig.FfprobePath.Should().Be("/opt/bin/ffprobe");
        sut.StatusText.Should().Contain("/opt/bin/ffmpeg");
        sut.PickFilesCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_WithoutTools_ExplainsWhatIsMissing()
    {
        ToolsAvailable(false);
        var sut = CreateSut();

        await sut.InitializeAsync();

        sut.IsReady.Should().BeFalse();
        sut.StatusText.Should().Contain("not found");
        sut.PickFilesCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_DiscoversOnlyOnce()
    {
        ToolsAvailable(true);
        var sut = CreateSut();

        await Task.WhenAll(sut.InitializeAsync(), sut.InitializeAsync());
        await sut.OpenFilesAsync([]);

        await _dependencies.Received(1).DiscoverAsync(Arg.Any<ExecutablePaths>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenFilesAsync_AddsProbedFilesInOrder()
    {
        ToolsAvailable(true);
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new SourceMediaInfo { SourcePath = call.Arg<string>() });
        var sut = CreateSut();

        await sut.OpenFilesAsync(["/media/a.mkv", "/media/b.mp4"]);

        sut.Files.Select(f => f.FileName).Should().Equal("a.mkv", "b.mp4");
        sut.Files.Should().OnlyContain(f => !f.IsError);
        sut.HasFiles.Should().BeTrue();
    }

    [Fact]
    public async Task OpenFilesAsync_WaitsForToolDiscoveryBeforeProbing()
    {
        var discovery = new TaskCompletionSource<DependencyStatus>();
        _dependencies.DiscoverAsync(Arg.Any<ExecutablePaths>(), Arg.Any<CancellationToken>())
            .Returns(discovery.Task);
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SourceMediaInfo { SourcePath = "/media/a.mkv" });
        var sut = CreateSut();

        var open = sut.OpenFilesAsync(["/media/a.mkv"]);
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        discovery.SetResult(Status(true));
        await open;

        sut.Files.Should().ContainSingle().Which.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task OpenFilesAsync_ProbeFailure_ShowsErrorForThatFileOnly()
    {
        ToolsAvailable(true);
        _probe.ProbeAsync("/media/broken.mkv", Arg.Any<CancellationToken>())
            .Returns<SourceMediaInfo>(_ => throw new MediaProbeException("Failed to parse ffprobe output: boom"));
        _probe.ProbeAsync("/media/good.mkv", Arg.Any<CancellationToken>())
            .Returns(new SourceMediaInfo { SourcePath = "/media/good.mkv" });
        var sut = CreateSut();

        await sut.OpenFilesAsync(["/media/broken.mkv", "/media/good.mkv"]);

        sut.Files.Should().HaveCount(2);
        sut.Files[0].IsError.Should().BeTrue();
        sut.Files[0].Summary.Should().Contain("boom");
        sut.Files[1].IsError.Should().BeFalse();
    }

    [Fact]
    public async Task OpenFilesAsync_WithoutTools_ListsFilesWithoutProbing()
    {
        ToolsAvailable(false);
        var sut = CreateSut();

        await sut.OpenFilesAsync(["/media/a.mkv"]);

        sut.Files.Should().ContainSingle().Which.IsError.Should().BeTrue();
        await _probe.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PickFilesCommand_OpensChosenFiles()
    {
        ToolsAvailable(true);
        _filePicker.PickMediaFilesAsync().Returns(["/media/picked.mkv"]);
        _probe.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new SourceMediaInfo { SourcePath = "/media/picked.mkv" });
        var sut = CreateSut();
        await sut.InitializeAsync();

        await sut.PickFilesCommand.ExecuteAsync(null);

        sut.Files.Should().ContainSingle().Which.FileName.Should().Be("picked.mkv");
    }

    [Fact]
    public async Task PickFilesCommand_Cancelled_LeavesListEmpty()
    {
        ToolsAvailable(true);
        _filePicker.PickMediaFilesAsync().Returns([]);
        var sut = CreateSut();
        await sut.InitializeAsync();

        await sut.PickFilesCommand.ExecuteAsync(null);

        sut.Files.Should().BeEmpty();
        sut.HasFiles.Should().BeFalse();
    }
}
