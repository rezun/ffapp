using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class ComparisonFrameServiceTests
{
    [Fact]
    public async Task Decode_CachesFramesByTimestampAndCrop_WithoutAnotherProcess()
    {
        var calls = 0;
        var runner = new Runner(async (command, ct) =>
        {
            calls++;
            await File.WriteAllBytesAsync(command.Arguments[^1], [1, 2, 3], ct);
            return new ProcessExecutionResult(command, JobState.Completed, 0, 0, "");
        });
        await using var service = new ComparisonFrameService(new ExecutablePathsConfig(), runner);
        var frame = await service.DecodeAsync("source", 1.125, null);
        (await service.DecodeAsync("source", 1.1250000001, null)).Should().BeSameAs(frame);
        calls.Should().Be(1);
        await service.DecodeAsync("source", 1.125, new CropRect(0, 0, 100, 100));
        calls.Should().Be(2);
        service.ClearCache();
        service.TryGetFrame("source", 1.125, null, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Cache_EvictsLeastRecentlyUsedFrames_WithinMemoryBudget()
    {
        var runner = new Runner(async (command, ct) =>
        {
            await File.WriteAllBytesAsync(command.Arguments[^1], [1, 2, 3], ct);
            return new ProcessExecutionResult(command, JobState.Completed, 0, 0, "");
        });
        await using var service = new ComparisonFrameService(new ExecutablePathsConfig(), runner, 6);
        await service.DecodeAsync("source", 1, null);
        await service.DecodeAsync("source", 2, null);
        service.TryGetFrame("source", 1, null, out _).Should().BeTrue();
        await service.DecodeAsync("source", 3, null);
        service.TryGetFrame("source", 1, null, out _).Should().BeTrue();
        service.TryGetFrame("source", 2, null, out _).Should().BeFalse();
        service.TryGetFrame("source", 3, null, out _).Should().BeTrue();
    }

    [Fact]
    public async Task Prefetch_DecodesConsecutiveFramesInOneProcess_AndFrameRequestsJoinIt()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        string? directory = null;
        var runner = new Runner(async (command, ct) =>
        {
            calls++;
            command.Arguments.Should().ContainInOrder("-frames:v", "4", "-fps_mode", "passthrough");
            command.Arguments.Should().ContainInOrder("-compression_level", "1");
            directory = Path.GetDirectoryName(command.Arguments[^1]) ?? throw new InvalidOperationException();
            started.SetResult();
            await release.Task.WaitAsync(ct);
            for (var i = 0; i < 4; i++)
                await File.WriteAllBytesAsync(Path.Combine(directory, $"{i:D2}.png"), [(byte)i], ct);
            return new ProcessExecutionResult(command, JobState.Completed, 0, 0, "");
        });
        await using var service = new ComparisonFrameService(new ExecutablePathsConfig(), runner);
        service.Prefetch("source", 1, 1.0 / 24, 4, null);
        var first = service.DecodeAsync("source", 1, null);
        var last = service.DecodeAsync("source", 1 + 3.0 / 24, null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        calls.Should().Be(1);
        first.IsCompleted.Should().BeFalse();
        release.SetResult();
        (await first).Should().Equal(0);
        (await last).Should().Equal(3);
        await service.DisposeAsync();
        Directory.Exists(directory).Should().BeFalse();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task CancellingOneFrameRequest_DoesNotCancelTheSharedPreload()
    {
        var release = new TaskCompletionSource();
        var preloadToken = CancellationToken.None;
        var calls = 0;
        var runner = new Runner(async (command, ct) =>
        {
            calls++;
            preloadToken = ct;
            await release.Task.WaitAsync(ct);
            var directory = Path.GetDirectoryName(command.Arguments[^1]) ?? throw new InvalidOperationException();
            await File.WriteAllBytesAsync(Path.Combine(directory, "00.png"), [7], ct);
            return new ProcessExecutionResult(command, JobState.Completed, 0, 0, "");
        });
        await using var service = new ComparisonFrameService(new ExecutablePathsConfig(), runner);
        service.Prefetch("source", 1, 1.0 / 24, 1, null);
        using var cancellation = new CancellationTokenSource();
        var cancelled = service.DecodeAsync("source", 1, null, cancellation.Token);
        cancellation.Cancel();
        var observe = () => cancelled;
        await observe.Should().ThrowAsync<OperationCanceledException>();
        preloadToken.IsCancellationRequested.Should().BeFalse();
        var nextRequest = service.DecodeAsync("source", 1, null);
        release.SetResult();
        (await nextRequest).Should().Equal(7);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task FailedPreload_FallsBackToSingleFrameDecode_ForAWaitingRequest()
    {
        var release = new TaskCompletionSource();
        var calls = 0;
        var runner = new Runner(async (command, ct) =>
        {
            calls++;
            if (command.Arguments[^1].EndsWith("%02d.png", StringComparison.Ordinal))
            {
                await release.Task.WaitAsync(ct);
                return new ProcessExecutionResult(command, JobState.Failed, 1, 0, "preload failed");
            }
            await File.WriteAllBytesAsync(command.Arguments[^1], [9], ct);
            return new ProcessExecutionResult(command, JobState.Completed, 0, 0, "");
        });
        await using var service = new ComparisonFrameService(new ExecutablePathsConfig(), runner);
        service.Prefetch("source", 1, 1.0 / 24, 1, null);
        var pending = service.DecodeAsync("source", 1, null);
        release.SetResult();
        (await pending).Should().Equal(9);
        calls.Should().Be(2);
    }

    [Fact]
    public async Task ClearCache_CancelsPreload_AndLateResultsCannotRepopulateCache()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = CancellationToken.None;
        string? directory = null;
        var runner = new Runner(async (command, ct) =>
        {
            token = ct;
            directory = Path.GetDirectoryName(command.Arguments[^1]) ?? throw new InvalidOperationException();
            started.SetResult();
            await release.Task; // Simulate a decoder completing after cancellation.
            await File.WriteAllBytesAsync(Path.Combine(directory, "00.png"), [1]);
            return new ProcessExecutionResult(command, JobState.Completed, 0, 0, "");
        });
        await using var service = new ComparisonFrameService(new ExecutablePathsConfig(), runner);
        service.Prefetch("source", 1, 1.0 / 24, 1, null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.ClearCache();
        token.IsCancellationRequested.Should().BeTrue();
        release.SetResult();
        await service.DisposeAsync();
        service.TryGetFrame("source", 1, null, out _).Should().BeFalse();
        Directory.Exists(directory).Should().BeFalse();
    }

    [Fact]
    public async Task Decode_UsesAccurateSeekAndCrop_ReadsImage_AndDeletesTemporaryFile()
    {
        GeneratedCommand? captured = null;
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner(async (command, ct) =>
        {
            captured = command;
            ct.Should().Be(cancellation.Token);
            await File.WriteAllBytesAsync(command.Arguments[^1], [1, 2, 3], ct);
            return new ProcessExecutionResult(command, JobState.Completed, 0, 0, "");
        });
        var paths = new ExecutablePathsConfig();
        paths.Configure(new ExecutablePaths("/tools/ffmpeg", "/tools/ffprobe"));
        var service = new ComparisonFrameService(paths, runner);

        var image = await service.DecodeAsync("/video with spaces.mkv", 123.125,
            new CropRect(10, 20, 640, 360), cancellation.Token);

        image.Should().Equal(1, 2, 3);
        var command = captured ?? throw new InvalidOperationException("No command captured.");
        command.ExecutablePath.Should().Be("/tools/ffmpeg");
        command.Protocol.Should().Be(ProcessProtocol.Plain);
        command.Arguments.Should().ContainInOrder("-ss", "123.125000000", "-i", "/video with spaces.mkv",
            "-vf", "crop=640:360:10:20", "-frames:v", "1");
        File.Exists(command.Arguments[^1]).Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decode_DeletesPartialImage_OnFailureOrCancellation(bool cancel)
    {
        string? output = null;
        using var cancellation = new CancellationTokenSource();
        var runner = new Runner(async (command, ct) =>
        {
            output = command.Arguments[^1];
            command.Arguments.Should().NotContain("-vf");
            await File.WriteAllBytesAsync(output, [1], ct);
            if (cancel) cancellation.Cancel();
            return new ProcessExecutionResult(command, JobState.Failed, 1, 0, "decode error");
        });
        var service = new ComparisonFrameService(new ExecutablePathsConfig(), runner);
        var decode = () => service.DecodeAsync("source", 0, null, cancellation.Token);

        if (cancel) await decode.Should().ThrowAsync<OperationCanceledException>();
        else await decode.Should().ThrowAsync<InvalidOperationException>().WithMessage("*decode error*");
        output.Should().NotBeNull();
        File.Exists(output).Should().BeFalse();
    }

    private sealed class Runner(Func<GeneratedCommand, CancellationToken, Task<ProcessExecutionResult>> run) : IProcessRunner
    {
        public Task<ProcessExecutionResult> RunAsync(GeneratedCommand command,
            double? expectedDurationSeconds = null, int? expectedFrameCount = null,
            Action<ProcessProgress>? progressCallback = null, Action<string>? logCallback = null,
            ExecutionPauseState? pauseState = null, CancellationToken ct = default) => run(command, ct);
    }
}
