using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Infrastructure;

namespace FFRux.Tests.Integration;

/// <summary>Skips a test when FFmpeg and FFprobe are not installed.</summary>
public sealed class FFmpegFactAttribute : FactAttribute
{
    public FFmpegFactAttribute()
    {
        if (!FFmpegFixture.ToolsInstalled)
            Skip = "FFmpeg and FFprobe are not installed.";
    }
}

/// <inheritdoc cref="FFmpegFactAttribute"/>
public sealed class FFmpegTheoryAttribute : TheoryAttribute
{
    public FFmpegTheoryAttribute()
    {
        if (!FFmpegFixture.ToolsInstalled)
            Skip = "FFmpeg and FFprobe are not installed.";
    }
}

[CollectionDefinition(Name)]
public sealed class FFmpegCollection : ICollectionFixture<FFmpegFixture>
{
    public const string Name = "FFmpeg";
}

/// <summary>
/// Generates one short source file per codec with FFmpeg's own encoders, so the tests need no
/// binary fixtures. Codecs without an encoder in the installed FFmpeg are reported as missing.
/// </summary>
public sealed class FFmpegFixture : IDisposable
{
    private static readonly Lazy<bool> Installed = new(() =>
        new DependencyManager().DiscoverAsync(ExecutablePaths.Empty).GetAwaiter().GetResult().IsReady);

    // codec_name -> FFmpeg encoder arguments and container for a one-second clip.
    private static readonly Dictionary<string, (string Kind, string[] Encode, string Extension)> Recipes = new(StringComparer.Ordinal)
    {
        ["h264"] = ("v", ["-c:v", "libx264", "-pix_fmt", "yuv420p"], ".mkv"),
        ["hevc"] = ("v", ["-c:v", "libx265", "-pix_fmt", "yuv420p", "-x265-params", "log-level=none"], ".mkv"),
        ["av1"] = ("v", ["-c:v", "libsvtav1", "-pix_fmt", "yuv420p"], ".mkv"),
        ["vp9"] = ("v", ["-c:v", "libvpx-vp9", "-b:v", "200k"], ".mkv"),
        ["vp8"] = ("v", ["-c:v", "libvpx", "-b:v", "200k"], ".mkv"),
        ["mpeg2video"] = ("v", ["-c:v", "mpeg2video"], ".mkv"),
        ["mpeg4"] = ("v", ["-c:v", "mpeg4"], ".mkv"),
        ["prores"] = ("v", ["-c:v", "prores_ks"], ".mkv"),
        ["mjpeg"] = ("v", ["-c:v", "mjpeg", "-pix_fmt", "yuvj420p"], ".mkv"),
        ["ffv1"] = ("v", ["-c:v", "ffv1"], ".mkv"),
        ["aac"] = ("a", ["-c:a", "aac"], ".mka"),
        ["ac3"] = ("a", ["-c:a", "ac3"], ".mka"),
        ["eac3"] = ("a", ["-c:a", "eac3"], ".mka"),
        ["dts"] = ("a", ["-ac", "2", "-c:a", "dca", "-strict", "-2"], ".mka"),
        ["truehd"] = ("a", ["-c:a", "truehd", "-strict", "-2"], ".mka"),
        ["flac"] = ("a", ["-c:a", "flac"], ".mka"),
        ["alac"] = ("a", ["-c:a", "alac"], ".mka"),
        ["opus"] = ("a", ["-c:a", "libopus"], ".mka"),
        ["vorbis"] = ("a", ["-c:a", "libvorbis"], ".mka"),
        ["mp3"] = ("a", ["-c:a", "libmp3lame"], ".mka"),
        ["mp2"] = ("a", ["-c:a", "mp2"], ".mka"),
        ["pcm_s16le"] = ("a", ["-c:a", "pcm_s16le"], ".mka"),
    };

    private readonly ConcurrentDictionary<string, Lazy<string?>> _sources = new(StringComparer.Ordinal);

    public FFmpegFixture()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("ffrux-it-").FullName;
        Paths = new ExecutablePathsConfig();
        if (ToolsInstalled)
            Paths.Configure(new DependencyManager().DiscoverAsync(ExecutablePaths.Empty).GetAwaiter().GetResult().ExecutablePaths);
    }

    public static bool ToolsInstalled => Installed.Value;

    private static readonly Lazy<HashSet<string>> Encoders = new(ListEncoders);

    /// <summary>True when the installed FFmpeg can create a source for the codec.</summary>
    public static bool CanEncode(string codec) =>
        ToolsInstalled && Recipes.TryGetValue(codec, out var recipe) && Encoders.Value.Contains(EncoderName(recipe.Encode));

    public string Directory { get; }
    public ExecutablePathsConfig Paths { get; }

    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);

    public string NewPath(string extension) => Path.Combine(Directory, $"{Guid.NewGuid():N}{extension}");

    /// <summary>A one-second file with a single track of the codec, or null when FFmpeg cannot encode it.</summary>
    public string? SourceFor(string codec) =>
        _sources.GetOrAdd(codec, c => new Lazy<string?>(() => Generate(c))).Value;

    /// <summary>Two seconds of H.264 video plus AAC audio, with two chapters and a title.</summary>
    public string MovieWithChapters() => _sources.GetOrAdd("movie", _ => new Lazy<string?>(() =>
    {
        var metadata = Path.Combine(Directory, "chapters.txt");
        File.WriteAllText(metadata, """
            ;FFMETADATA1
            title=Container title
            [CHAPTER]
            TIMEBASE=1/1000
            START=0
            END=1000
            title=Intro
            [CHAPTER]
            TIMEBASE=1/1000
            START=1000
            END=2000
            title=Main
            """);
        var output = Path.Combine(Directory, "movie.mkv");
        Run(Paths.FfmpegPath, ["-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=2",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=2", "-i", metadata,
            "-map", "0", "-map", "1", "-map_metadata", "2", "-map_chapters", "2",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", output]);
        return output;
    })).Value ?? throw new InvalidOperationException("Could not create the movie fixture.");

    public string TextFile(string name, byte[] content)
    {
        var path = Path.Combine(Directory, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    /// <summary>Runs a process and returns its exit code and output.</summary>
    public static (int ExitCode, string Stdout, string Stderr) Run(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {executable}.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result, stderr);
    }

    /// <summary>FFprobe's JSON for streams, chapters and format of a file.</summary>
    public JsonElement Probe(string path, params string[] extra)
    {
        var (exitCode, stdout, stderr) = Run(Paths.FfprobePath,
            ["-v", "error", "-show_streams", "-show_chapters", "-show_format", .. extra, "-of", "json", path]);
        if (exitCode != 0)
            throw new InvalidOperationException($"ffprobe failed: {stderr}");
        return JsonDocument.Parse(stdout).RootElement.Clone();
    }

    private static string EncoderName(string[] encode) =>
        encode[Array.FindIndex(encode, arg => arg is "-c:v" or "-c:a") + 1];

    private static HashSet<string> ListEncoders()
    {
        var ffmpeg = new DependencyManager().DiscoverAsync(ExecutablePaths.Empty).GetAwaiter().GetResult().ExecutablePaths.Ffmpeg ?? "ffmpeg";
        var (_, stdout, _) = Run(ffmpeg, ["-hide_banner", "-encoders"]);
        // Lines look like " V....D libx264    libx264 H.264 ..."; the second column is the encoder name.
        return [.. stdout.Split('\n')
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length > 1 && parts[0].Length == 6)
            .Select(parts => parts[1])];
    }

    private string? Generate(string codec)
    {
        var (kind, encode, extension) = Recipes[codec];
        string[] input = kind == "v"
            ? ["-f", "lavfi", "-i", "testsrc2=size=320x240:rate=25:duration=1"]
            : ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=1"];
        var output = Path.Combine(Directory, codec + extension);
        var (exitCode, _, _) = Run(Paths.FfmpegPath, ["-nostdin", "-y", "-loglevel", "error", .. input, .. encode, output]);
        return exitCode == 0 ? output : null;
    }
}
