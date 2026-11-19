using FFApp.Services;
using FFApp.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public sealed class AppUpdateViewModelTests
{
    private readonly FakeSource _source = new();
    private readonly FakeHost _host = new();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private AppUpdateViewModel Create() => new(_source, _host, _dialogs, NullLogger<AppUpdateViewModel>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NoUpdate_OnlyManualChecksLeaveAStatusMessage(bool manual)
    {
        var sut = Create();
        await sut.CheckAsync(manual);
        sut.IsVisible.Should().Be(manual);
        sut.CanRestart.Should().BeFalse();
        await _dialogs.DidNotReceive().ShowMessageAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NetworkFailure_LeavesTheAppUsableAndOnlyReportsManualFailures(bool manual)
    {
        _source.CheckFailure = new HttpRequestException("Offline");
        var sut = Create();
        await sut.CheckAsync(manual);
        sut.IsBusy.Should().BeFalse();
        sut.IsVisible.Should().Be(manual);
        sut.CanRestart.Should().BeFalse();
        await _dialogs.DidNotReceive().ShowErrorAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Fact]
    public async Task ConcurrentChecks_KeepOneRequestInFlight()
    {
        _source.CheckGate = new TaskCompletionSource();
        var sut = Create();
        var first = sut.CheckAsync(false);
        await sut.CheckAsync(true);
        _source.Checks.Should().Be(1);
        sut.IsBusy.Should().BeTrue();
        _source.CheckGate.SetResult();
        await first;
        sut.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task AvailableUpdate_IsDownloadedInTheBackgroundAndRetriedAfterAFailure()
    {
        _source.Available = new("2.0.0", true, false);
        _source.DownloadFailure = new IOException("Connection lost");
        var sut = Create();
        await sut.CheckAsync(false);
        sut.CanRestart.Should().BeFalse();
        sut.IsVisible.Should().BeFalse();
        await _dialogs.DidNotReceive().ShowErrorAsync(Arg.Any<string>(), Arg.Any<string>());
        _source.DownloadFailure = null;
        await sut.CheckAsync(false);
        sut.CanRestart.Should().BeTrue();
        sut.IsVisible.Should().BeTrue();
        _source.Downloads.Should().Be(2);
        _source.Installs.Should().Be(0);
    }

    [Fact]
    public async Task LocalBuild_OffersTheReleasePageInsteadOfAttemptingASelfUpdate()
    {
        _source.Available = new("2.0.0", false, false);
        var sut = Create();
        await sut.CheckAsync(false);
        sut.CanOpenDownloads.Should().BeTrue();
        await sut.OpenDownloadsCommand.ExecuteAsync(null);
        _host.OpenedDownloads.Should().BeTrue();
        _source.Downloads.Should().Be(0);
        sut.CanRestart.Should().BeFalse();
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task PreviouslyDownloadedUpdate_InstallsOnlyAfterSuccessfulPreparation(bool prepared, int installs)
    {
        _source.Available = new("2.0.0", true, true);
        _host.CanRestart = prepared;
        var sut = Create();
        await sut.CheckAsync(false);
        sut.CanRestart.Should().BeTrue();
        await sut.RestartNowCommand.ExecuteAsync(null);
        _source.Installs.Should().Be(installs);
        _source.Downloads.Should().Be(0);
        sut.IsBusy.Should().BeFalse();
        sut.CanRestart.Should().BeTrue();
    }

    [Fact]
    public async Task FailedInstall_PreservesTheVerifiedDownloadForAnotherAttempt()
    {
        _source.Available = new("2.0.0", true, true);
        _source.InstallFailure = new IOException("Updater could not start");
        var sut = Create();
        await sut.CheckAsync(false);
        await sut.RestartNowCommand.ExecuteAsync(null);
        sut.CanRestart.Should().BeTrue();
        sut.IsBusy.Should().BeFalse();
        await _dialogs.Received(1).ShowErrorAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    private sealed class FakeSource : IAppUpdateSource
    {
        public AppUpdate? Available { get; set; }
        public Exception? CheckFailure { get; set; }
        public Exception? DownloadFailure { get; set; }
        public Exception? InstallFailure { get; set; }
        public TaskCompletionSource? CheckGate { get; set; }
        public int Checks { get; private set; }
        public int Downloads { get; private set; }
        public int Installs { get; private set; }
        public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
        {
            Checks++;
            if (CheckGate is not null) await CheckGate.Task.WaitAsync(cancellationToken);
            if (CheckFailure is not null) throw CheckFailure;
            return Available;
        }
        public Task DownloadAsync(IProgress<int> progress, CancellationToken cancellationToken = default)
        {
            Downloads++;
            if (DownloadFailure is not null) throw DownloadFailure;
            return Task.CompletedTask;
        }
        public void ApplyAndRestart()
        {
            if (InstallFailure is not null) throw InstallFailure;
            Installs++;
        }
    }

    private sealed class FakeHost : IUpdateHost
    {
        public bool OpenedDownloads { get; private set; }
        public bool CanRestart { get; set; } = true;
        public Task OpenDownloadsAsync()
        {
            OpenedDownloads = true;
            return Task.CompletedTask;
        }
        public Task<bool> RestartAsync(Action applyUpdate)
        {
            if (CanRestart) applyUpdate();
            return Task.FromResult(CanRestart);
        }
    }
}
