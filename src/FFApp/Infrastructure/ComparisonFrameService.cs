using System.Globalization;
using System.Diagnostics;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;

namespace FFApp.Infrastructure;

public sealed class ComparisonFrameService(ExecutablePathsConfig paths, IProcessRunner runner)
    : IComparisonFrameService, IDisposable, IAsyncDisposable
{
    private static readonly long CacheBudgetBytes = 96 * 1024 * 1024;
    private readonly long _cacheBudgetBytes = CacheBudgetBytes;
    private readonly object _gate = new();
    private readonly Dictionary<FrameKey, byte[]> _cache = [];
    private readonly LinkedList<FrameKey> _recency = [];
    private readonly Dictionary<FrameKey, TaskCompletionSource<byte[]>> _pending = [];
    private readonly Dictionary<TrackKey, CancellationTokenSource> _prefetches = [];
    private readonly List<Task> _backgroundTasks = [];
    private long _cacheBytes;
    private int _generation;
    private bool _disposed;

    internal ComparisonFrameService(ExecutablePathsConfig paths, IProcessRunner runner, long cacheBudgetBytes)
        : this(paths, runner) => _cacheBudgetBytes = cacheBudgetBytes;

    private sealed record TrackKey(string Path, CropRect? Crop);
    private sealed record FrameKey(TrackKey Track, long Microseconds);
    private static FrameKey Key(string path, double seconds, CropRect? crop) =>
        new(new TrackKey(path, crop), (long)Math.Round(seconds * 1_000_000));

    public bool TryGetFrame(string path, double seconds, CropRect? crop, out byte[] frame)
    {
        lock (_gate) return TryGetFrame(Key(path, seconds, crop), out frame);
    }

    private bool TryGetFrame(FrameKey key, out byte[] frame)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            _recency.Remove(key);
            _recency.AddLast(key);
            frame = cached;
            return true;
        }
        frame = [];
        return false;
    }

    private void Remember(FrameKey key, byte[] frame, int generation)
    {
        if (_disposed || generation != _generation) return;
        if (_cache.Remove(key, out var old)) _cacheBytes -= old.Length;
        _recency.Remove(key);
        _cache[key] = frame;
        _recency.AddLast(key);
        _cacheBytes += frame.Length;
        while (_cacheBytes > _cacheBudgetBytes && _recency.First is { } first)
        {
            _cacheBytes -= _cache[first.Value].Length;
            _cache.Remove(first.Value);
            _recency.RemoveFirst();
        }
    }

    /// <summary>
    /// Decodes one frame. Cancellation terminates FFmpeg through the process runner;
    /// the temporary image is deleted on success, failure, and cancellation.
    /// </summary>
    public async Task<byte[]> DecodeAsync(string path, double seconds, CropRect? crop, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var key = Key(path, seconds, crop);
        Task<byte[]>? pending;
        CancellationTokenSource? obsolete;
        int generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (TryGetFrame(key, out var cached)) return cached;
            pending = _pending.TryGetValue(key, out var completion) ? completion.Task : null;
            obsolete = pending is null ? _prefetches.GetValueOrDefault(key.Track) : null;
            obsolete?.Cancel();
            generation = _generation;
        }
        // A frame step can join the already running sequential decode. Cancelling
        // that caller does not cancel the preload needed by the next click.
        if (pending is not null)
        {
            try { return await pending.WaitAsync(ct); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return await DecodeAsync(path, seconds, crop, ct);
            }
        }
        var output = Path.Combine(Path.GetTempPath(), $"ffapp-compare-{Guid.NewGuid():N}.png");
        try
        {
            var arguments = new List<string>
            {
                "-v", "error", "-nostdin", "-ss", seconds.ToString("F9", CultureInfo.InvariantCulture),
                "-i", path, "-map", "0:v:0", "-an", "-sn",
            };
            if (crop is { Width: > 0, Height: > 0 })
                arguments.AddRange(["-vf", FormattableString.Invariant($"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}")]);
            arguments.AddRange(["-frames:v", "1", "-threads", "1", "-compression_level", "1", "-y", output]);
            var result = await runner.RunAsync(new GeneratedCommand(paths.FfmpegPath, [.. arguments], "", ProcessProtocol.Plain), ct: ct);
            ct.ThrowIfCancellationRequested();
            if (result.State != JobState.Completed || !File.Exists(output))
                throw new InvalidOperationException($"Could not decode comparison frame: {result.StderrOutput}");
            var frame = await File.ReadAllBytesAsync(output, ct);
            lock (_gate) Remember(key, frame, generation);
            return frame;
        }
        finally
        {
            File.Delete(output);
        }
    }

    public void Prefetch(string path, double seconds, double frameDuration, int count, CropRect? crop)
    {
        if (count <= 0) return;
        var track = new TrackKey(path, crop);
        lock (_gate)
        {
            if (_disposed || _prefetches.ContainsKey(track)) return;
            var skip = 0;
            while (skip < count && _cache.ContainsKey(Key(path, seconds + skip * frameDuration, crop))) skip++;
            if (skip == count) return;
            // Refill in batches once fewer than half the look-ahead frames remain.
            // Refilling a single missing tail frame on every click repeats seeks.
            if (skip >= Math.Max(1, count / 2)) return;
            seconds += skip * frameDuration;
            count -= skip;
            var keys = Enumerable.Range(0, count).Select(i => Key(path, seconds + i * frameDuration, crop)).ToArray();
            var cancellation = new CancellationTokenSource();
            _prefetches.Add(track, cancellation);
            foreach (var key in keys) _pending[key] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _backgroundTasks.RemoveAll(task => task.IsCompleted);
            var generation = _generation;
            // Starting a process and creating temporary files must not delay a
            // cached frame's paint on the UI thread. Retain and await on disposal.
            _backgroundTasks.Add(Task.Run(() => PrefetchCoreAsync(track, seconds, keys, cancellation, generation)));
        }
    }

    private async Task PrefetchCoreAsync(TrackKey track, double seconds, FrameKey[] keys,
        CancellationTokenSource cancellation, int generation)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ffapp-compare-{Guid.NewGuid():N}");
        // This is the boundary for tracked background decoding. Each exit releases
        // waiting frame requests, terminates the process through cancellation, and
        // deletes all partial images.
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(directory);
            var arguments = new List<string>
            {
                "-v", "error", "-nostdin", "-ss", seconds.ToString("F9", CultureInfo.InvariantCulture),
                "-i", track.Path, "-map", "0:v:0", "-an", "-sn",
            };
            if (track.Crop is { Width: > 0, Height: > 0 } crop)
                arguments.AddRange(["-vf", FormattableString.Invariant($"crop={crop.Width}:{crop.Height}:{crop.X}:{crop.Y}")]);
            arguments.AddRange(["-frames:v", keys.Length.ToString(CultureInfo.InvariantCulture), "-fps_mode", "passthrough",
                "-threads", "1", "-compression_level", "1", "-start_number", "0", "-y", Path.Combine(directory, "%02d.png")]);
            var result = await runner.RunAsync(new GeneratedCommand(paths.FfmpegPath, [.. arguments], "", ProcessProtocol.Plain),
                ct: cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.State != JobState.Completed)
                throw new InvalidOperationException($"Could not preload comparison frames: {result.StderrOutput}");
            for (var i = 0; i < keys.Length; i++)
            {
                var file = Path.Combine(directory, i.ToString("D2", CultureInfo.InvariantCulture) + ".png");
                if (!File.Exists(file)) break;
                var frame = await File.ReadAllBytesAsync(file, cancellation.Token);
                lock (_gate)
                {
                    if (generation != _generation) return;
                    Remember(keys[i], frame, generation);
                    if (_pending.Remove(keys[i], out var completion)) completion.TrySetResult(frame);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { Debug.WriteLine($"Comparison frame preloading failed: {ex}"); }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    foreach (var key in keys)
                        if (_pending.Remove(key, out var completion)) completion.TrySetCanceled();
                    if (ReferenceEquals(_prefetches.GetValueOrDefault(track), cancellation)) _prefetches.Remove(track);
                }
                cancellation.Dispose();
            }
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            catch (IOException ex) { Debug.WriteLine($"Comparison preload cleanup failed: {ex}"); }
            catch (UnauthorizedAccessException ex) { Debug.WriteLine($"Comparison preload cleanup failed: {ex}"); }
        }
    }

    public void ClearCache()
    {
        lock (_gate)
        {
            _generation++;
            foreach (var cancellation in _prefetches.Values) cancellation.Cancel();
            foreach (var completion in _pending.Values) completion.TrySetCanceled();
            _prefetches.Clear();
            _pending.Clear();
            _cache.Clear();
            _recency.Clear();
            _cacheBytes = 0;
        }
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; ClearCache(); }
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] tasks;
        lock (_gate) tasks = _backgroundTasks.ToArray();
        await Task.WhenAll(tasks);
    }
}
