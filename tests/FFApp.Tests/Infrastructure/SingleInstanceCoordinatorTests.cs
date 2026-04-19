using System.IO.Pipes;
using System.Text;
using FFApp.Infrastructure.SingleInstance;
using FluentAssertions;

namespace FFApp.Tests.Infrastructure;

public sealed class SingleInstanceCoordinatorTests
{
    // Unix domain sockets under /tmp cap the full path at 104 chars on macOS, so keep the pipe name short.
    private static string UniqueName() => $"ff{Guid.NewGuid():N}"[..18];

    private static (string lockPath, string pipeName) UniqueKeys()
    {
        var name = UniqueName();
        return (Path.Combine(Path.GetTempPath(), $"{name}.lock"), name);
    }

    private static SingleInstanceCoordinator NewCoordinator((string lockPath, string pipeName) keys)
        => new(keys.lockPath, keys.pipeName);

    [Fact]
    public async Task TryAcquire_OnFirstCall_ReturnsTrue()
    {
        var keys = UniqueKeys();
        await using var coordinator = NewCoordinator(keys);

        coordinator.TryAcquire().Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquire_WhenAnotherInstanceHoldsLock_ReturnsFalse()
    {
        var keys = UniqueKeys();
        await using var primary = NewCoordinator(keys);
        await using var secondary = NewCoordinator(keys);

        primary.TryAcquire().Should().BeTrue();
        secondary.TryAcquire().Should().BeFalse();
    }

    [Fact]
    public async Task TryAcquire_AfterDispose_AllowsNewInstance()
    {
        var keys = UniqueKeys();
        var primary = NewCoordinator(keys);
        primary.TryAcquire().Should().BeTrue();
        await primary.DisposeAsync();

        await using var next = NewCoordinator(keys);
        next.TryAcquire().Should().BeTrue();
    }

    [Fact]
    public async Task TryAcquire_CalledTwice_Throws()
    {
        var keys = UniqueKeys();
        await using var coordinator = NewCoordinator(keys);
        coordinator.TryAcquire().Should().BeTrue();

        var act = () => coordinator.TryAcquire();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task SendActivationAsync_DeliversArgsToPrimaryInstance()
    {
        var keys = UniqueKeys();
        await using var primary = NewCoordinator(keys);
        primary.TryAcquire().Should().BeTrue();

        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += (_, e) => received.TrySetResult(e.Args);

        await using var secondary = NewCoordinator(keys);
        await secondary.SendActivationAsync(new[] { "foo.mp4", "--preset=fast" });

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(received.Task, "activation should be delivered within 5s");
        (await received.Task).Should().Equal("foo.mp4", "--preset=fast");
    }

    [Fact]
    public async Task PipeServer_ContinuesAfterMalformedConnection()
    {
        var keys = UniqueKeys();
        await using var primary = NewCoordinator(keys);
        primary.TryAcquire().Should().BeTrue();

        var received = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ActivationRequested += (_, e) => received.TrySetResult(e.Args);

        await using (var garbage = new NamedPipeClientStream(".", keys.pipeName, PipeDirection.Out, PipeOptions.Asynchronous))
        {
            await garbage.ConnectAsync(2000);
            var bytes = Encoding.UTF8.GetBytes("NOT_AN_ACTIVATION\n");
            await garbage.WriteAsync(bytes);
        }

        await Task.Delay(100);

        await using var secondary = NewCoordinator(keys);
        await secondary.SendActivationAsync(new[] { "real.mp4" });

        var completed = await Task.WhenAny(received.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.Should().Be(received.Task, "server should recover and accept a follow-up activation");
        (await received.Task).Should().Equal("real.mp4");
    }

    [Fact]
    public async Task SendActivationAsync_WhenNoPrimary_ThrowsTimeout()
    {
        var keys = UniqueKeys();
        await using var coordinator = NewCoordinator(keys);

        var act = async () => await coordinator.SendActivationAsync(Array.Empty<string>());
        await act.Should().ThrowAsync<TimeoutException>();
    }
}
