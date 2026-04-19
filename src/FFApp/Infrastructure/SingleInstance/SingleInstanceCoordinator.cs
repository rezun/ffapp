using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace FFApp.Infrastructure.SingleInstance;

public sealed class SingleInstanceActivationEventArgs(IReadOnlyList<string> args) : EventArgs
{
    public IReadOnlyList<string> Args { get; } = args;
}

public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private const string ActivationTag = "ACTIVATE";
    private static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(2);

    private readonly string _lockFilePath;
    private readonly string _pipeName;
    private FileStream? _lockFile;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;

    public event EventHandler<SingleInstanceActivationEventArgs>? ActivationRequested;

    public SingleInstanceCoordinator(string lockFilePath, string pipeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _lockFilePath = lockFilePath;
        _pipeName = pipeName;
    }

    public static SingleInstanceCoordinator CreateDefault()
    {
        var userHash = ComputeUserHash(Environment.UserName);
        var key = $"FFApp.SingleInstance.{userHash}";
        var appData = AppPaths.AppDataDirectory();
        Directory.CreateDirectory(appData);
        var lockPath = Path.Combine(appData, "instance.lock");
        return new SingleInstanceCoordinator(lockPath, key);
    }

    public bool TryAcquire()
    {
        if (_lockFile is not null)
            throw new InvalidOperationException("SingleInstanceCoordinator already acquired.");

        FileStream lockFile;
        try
        {
            lockFile = new FileStream(
                _lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        _lockFile = lockFile;
        _serverCts = new CancellationTokenSource();
        _serverTask = Task.Run(() => RunPipeServerAsync(_serverCts.Token));
        return true;
    }

    public void SendActivation(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        using var client = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.Out);

        client.Connect((int)DefaultSendTimeout.TotalMilliseconds);

        using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = false };
        writer.WriteLine(ActivationTag);
        foreach (var arg in args)
            writer.WriteLine(arg);
        writer.Flush();
    }

    public async Task SendActivationAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        await using var client = new NamedPipeClientStream(
            serverName: ".",
            pipeName: _pipeName,
            direction: PipeDirection.Out,
            options: PipeOptions.Asynchronous);

        await client.ConnectAsync(DefaultSendTimeout, cancellationToken).ConfigureAwait(false);

        using var writer = new StreamWriter(client, Encoding.UTF8) { AutoFlush = false };
        await writer.WriteLineAsync(ActivationTag).ConfigureAwait(false);
        foreach (var arg in args)
            await writer.WriteLineAsync(arg).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    pipeName: _pipeName,
                    direction: PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    transmissionMode: PipeTransmissionMode.Byte,
                    options: PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var payload = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var args = ParseActivationMessage(payload);
                if (args is not null)
                    ActivationRequested?.Invoke(this, new SingleInstanceActivationEventArgs(args));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Client disconnected abruptly; loop and accept the next connection.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            finally
            {
                if (server is not null)
                    await server.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static IReadOnlyList<string>? ParseActivationMessage(string payload)
    {
        var lines = payload.Split('\n');
        if (lines.Length == 0)
            return null;
        var head = lines[0].TrimEnd('\r');
        if (!string.Equals(head, ActivationTag, StringComparison.Ordinal))
            return null;
        var args = new List<string>(lines.Length - 1);
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length > 0)
                args.Add(line);
        }
        return args;
    }

    private static string ComputeUserHash(string userName)
    {
        var bytes = Encoding.UTF8.GetBytes(userName);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6);
    }

    public async ValueTask DisposeAsync()
    {
        if (_serverCts is not null)
        {
            try
            {
                await _serverCts.CancelAsync().ConfigureAwait(false);
            }
            catch (ObjectDisposedException) { }

            if (_serverTask is not null)
            {
                try { await _serverTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            _serverCts.Dispose();
            _serverCts = null;
            _serverTask = null;
        }

        if (_lockFile is not null)
        {
            await _lockFile.DisposeAsync().ConfigureAwait(false);
            _lockFile = null;
            try { File.Delete(_lockFilePath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
