using System.Globalization;
using System.Text;
using System.Text.Json;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Infrastructure;
using FFApp.Infrastructure.Logging;
using FFRux.Core;
using FFRux.Core.Models;
using FFRux.Infrastructure;
using FluentAssertions;

namespace FFRux.Tests.Integration;

/// <summary>
/// Runs the rule table and the generated commands against the installed FFmpeg (requirements 8.3).
/// A new FFmpeg version that changes what a muxer accepts shows up here as a failing test.
/// </summary>
[Collection(FFmpegCollection.Name)]
[Trait("Category", "Integration")]
public sealed class RemuxIntegrationTests(FFmpegFixture ffmpeg)
{
    private readonly MediaProbeService _probe = new(ffmpeg.Paths);
    private RemuxCommandBuilder Builder => new(ffmpeg.Paths);

    public static TheoryData<string, OutputFormat> AllowedEntries()
    {
        var data = new TheoryData<string, OutputFormat>();
        foreach (var (codec, row) in CodecRules.Video.Concat(CodecRules.Audio))
        {
            var fixtureCodec = codec == CodecRules.PcmKey ? "pcm_s16le" : codec;
            if (!FFmpegFixture.CanEncode(fixtureCodec))
                continue;
            foreach (var format in row.Keys)
                data.Add(fixtureCodec, format);
        }
        return data;
    }

    [FFmpegTheory]
    [MemberData(nameof(AllowedEntries))]
    public async Task EveryAllowedEntry_Muxes(string codec, OutputFormat format)
    {
        var source = ffmpeg.SourceFor(codec) ?? throw new InvalidOperationException($"Could not create a {codec} source.");

        var output = await RemuxAsync(await PlanAsync(format, source));

        StreamCodecs(output).Should().Equal(codec);
    }

    // Combinations the muxers themselves refuse; FFRux marks them incompatible. If FFmpeg starts
    // accepting one of them, the rule table may be too strict and should be reviewed.
    public static TheoryData<string, string> KnownRefusals()
    {
        (string Codec, string Muxer)[] refusals =
        [
            ("prores", "mp4"), ("av1", "mov"), ("vp9", "mov"), ("vp8", "mp4"), ("truehd", "mp4"),
            ("eac3", "ipod"), ("mp3", "ipod"), ("flac", "mov"), ("opus", "mov"),
        ];
        var data = new TheoryData<string, string>();
        foreach (var (codec, muxer) in refusals.Where(r => FFmpegFixture.CanEncode(r.Codec)))
            data.Add(codec, muxer);
        return data;
    }

    [FFmpegTheory]
    [MemberData(nameof(KnownRefusals))]
    public void KnownRefusals_StillFail(string codec, string muxer)
    {
        var source = ffmpeg.SourceFor(codec) ?? throw new InvalidOperationException($"Could not create a {codec} source.");

        var (exitCode, _, _) = FFmpegFixture.Run(ffmpeg.Paths.FfmpegPath,
            ["-nostdin", "-y", "-loglevel", "error", "-i", source, "-map", "0", "-c", "copy", "-f", muxer, ffmpeg.NewPath(".out")]);

        exitCode.Should().NotBe(0);
    }

    [FFmpegTheory]
    [InlineData("subrip", OutputFormat.Mkv, "subrip")]
    [InlineData("subrip", OutputFormat.Mp4, "mov_text")]
    [InlineData("subrip", OutputFormat.Webm, "webvtt")]
    [InlineData("ass", OutputFormat.Mkv, "ass")]
    [InlineData("ass", OutputFormat.Mov, "mov_text")]
    [InlineData("webvtt", OutputFormat.Webm, "webvtt")]
    [InlineData("webvtt", OutputFormat.Mp4, "mov_text")]
    [InlineData("mov_text", OutputFormat.Mkv, "subrip")]
    [InlineData("mov_text", OutputFormat.Mp4, "mov_text")]
    public async Task TextSubtitles_AreCopiedOrConverted(string codec, OutputFormat format, string expected)
    {
        var video = format == OutputFormat.Webm ? "vp9" : "h264";
        var plan = await PlanAsync(format, ffmpeg.SourceFor(video)!, await SubtitleSourceAsync(codec));

        var output = await RemuxAsync(plan);

        StreamCodecs(output).Should().Equal(video, expected);
    }

    [FFmpegFact]
    public async Task Matroska_StoresNameLanguageAndAllFlags()
    {
        var plan = (await PlanAsync(OutputFormat.Mkv, ffmpeg.SourceFor("aac")!)).WithAllTracks(s => s with
        {
            Name = "Director's commentary",
            Language = Languages.Find("ger")!,
            Flags = TrackFlags.Default | TrackFlags.Commentary | TrackFlags.Original | TrackFlags.VisualImpaired,
        });

        var stream = Streams(await RemuxAsync(plan))[0];

        stream.GetProperty("tags").GetProperty("title").GetString().Should().Be("Director's commentary");
        stream.GetProperty("tags").GetProperty("language").GetString().Should().Be("ger");
        var disposition = stream.GetProperty("disposition");
        disposition.GetProperty("default").GetInt32().Should().Be(1);
        disposition.GetProperty("comment").GetInt32().Should().Be(1);
        disposition.GetProperty("original").GetInt32().Should().Be(1);
        disposition.GetProperty("visual_impaired").GetInt32().Should().Be(1);
        disposition.GetProperty("forced").GetInt32().Should().Be(0);
    }

    [FFmpegFact]
    public async Task Mp4_StoresTheNameAsHandlerName()
    {
        var plan = (await PlanAsync(OutputFormat.Mp4, ffmpeg.SourceFor("aac")!))
            .WithAllTracks(s => s with { Name = "Stereo", Language = Languages.Find("fre")!, Flags = TrackFlags.Commentary });

        var stream = Streams(await RemuxAsync(plan))[0];

        stream.GetProperty("tags").GetProperty("handler_name").GetString().Should().Be("Stereo");
        stream.GetProperty("tags").GetProperty("language").GetString().Should().Be("fre");
        stream.GetProperty("disposition").GetProperty("comment").GetInt32().Should().Be(1);
    }

    [FFmpegFact]
    public async Task QuickTime_StoresLanguagesThroughItsOwnCodes()
    {
        var plan = (await PlanAsync(OutputFormat.Mov, ffmpeg.SourceFor("aac")!)).WithAllTracks(s => s with { Language = Languages.Find("fre")! });

        Streams(await RemuxAsync(plan))[0].GetProperty("tags").GetProperty("language").GetString().Should().Be("fra");
    }

    [FFmpegTheory]
    [InlineData(OutputFormat.Mkv)]
    [InlineData(OutputFormat.Mp4)]
    [InlineData(OutputFormat.Mov)]
    [InlineData(OutputFormat.Webm)]
    public async Task ChaptersAndTitle_AreWritten(OutputFormat format)
    {
        var source = format == OutputFormat.Webm ? await WebmMovieAsync() : ffmpeg.MovieWithChapters();
        var plan = await PlanAsync(format, source) with { ChapterSourceNumber = 1, Title = "Interstellar" };

        var probe = ffmpeg.Probe(await RemuxAsync(plan));

        probe.GetProperty("chapters").GetArrayLength().Should().Be(2);
        probe.GetProperty("format").GetProperty("tags").GetProperty("title").GetString().Should().Be("Interstellar");
    }

    [FFmpegFact]
    public async Task NoChapters_RemovesThem()
    {
        var plan = await PlanAsync(OutputFormat.Mkv, ffmpeg.MovieWithChapters());

        ffmpeg.Probe(await RemuxAsync(plan)).GetProperty("chapters").GetArrayLength().Should().Be(0);
    }

    [FFmpegTheory]
    [InlineData(OutputFormat.Mkv, -250)]
    [InlineData(OutputFormat.Mkv, 250)]
    [InlineData(OutputFormat.Mp4, -250)]
    public async Task Timeshift_MovesAudioAgainstVideoExactly(OutputFormat format, int shiftMs)
    {
        var movie = ffmpeg.MovieWithChapters();
        var plan = (await PlanAsync(format, movie)).WithTrack(1, 1, s => s with { TimeshiftMs = shiftMs });
        var sourceAudio = PacketTimes(movie, "a");

        var output = await RemuxAsync(plan);

        PacketTimes(output, "v")[0].Should().BeApproximately(0, 0.001, "video is the reference");
        var shifted = PacketTimes(output, "a");
        // The packet that was at t is now at t + shift; with a negative shift the earlier packets are dropped.
        var expected = sourceAudio.Select(t => t + shiftMs / 1000.0).Where(t => t >= -0.001).ToList();
        shifted.Take(5).Should().Equal(expected.Take(5), (a, b) => Math.Abs(a - b) < 0.002);
    }

    [FFmpegFact]
    public async Task Windows1252Subtitles_AreConvertedToUtf8()
    {
        var srt = ffmpeg.TextFile("umlaut.de.srt",
            [.. Encoding.ASCII.GetBytes("1\r\n00:00:00,100 --> 00:00:00,900\r\nGr"), 0xFC, 0xDF, 0x65, (byte)'\r', (byte)'\n']);
        var plan = await PlanWithCharsetAsync(OutputFormat.Mkv, srt, "CP1252");

        var output = await RemuxAsync(plan);

        var (_, text, _) = FFmpegFixture.Run(ffmpeg.Paths.FfmpegPath, ["-nostdin", "-loglevel", "error", "-i", output, "-map", "0:s:0", "-f", "srt", "-"]);
        text.Should().Contain("Grüße");
    }

    [FFmpegFact]
    public async Task JobRunner_WritesTheOutputAndLeavesNoPartialFile()
    {
        var plan = await PlanAsync(OutputFormat.Mkv, ffmpeg.MovieWithChapters());
        var output = ffmpeg.NewPath(".mkv");
        var log = new List<string>();

        var result = await Runner().RunAsync(plan with { OutputPath = output }, log: log.Add);

        result.Outcome.Should().Be(RemuxOutcome.Completed);
        File.Exists(output).Should().BeTrue();
        File.Exists(OutputPaths.PartialFile(output)).Should().BeFalse();
        log.Should().Contain("[FFRux] Test remux passed.");

        result.LogPath.Should().NotBeNull();
        var saved = await File.ReadAllTextAsync(result.LogPath!);
        saved.Should().Contain($"Output:    {output}")
            .And.Contain("[FFRux] Test remux passed.")
            .And.Contain("Result:    Completed");
    }

    [FFmpegFact]
    public async Task JobRunner_StopsWhenTheTestRemuxFails()
    {
        // The rule table would block VP8 in MP4; the runner must still catch it on its own.
        var plan = await PlanAsync(OutputFormat.Mp4, ffmpeg.SourceFor("vp8")!);
        var output = ffmpeg.NewPath(".mp4");

        var result = await Runner().RunAsync(plan with { OutputPath = output });

        result.Outcome.Should().Be(RemuxOutcome.Failed);
        result.Error.Should().StartWith("FFmpeg rejected this combination in a test remux");
        File.Exists(output).Should().BeFalse();
    }

    [FFmpegFact]
    public async Task JobRunner_CancelKeepsAnExistingOutputFile()
    {
        var plan = await PlanAsync(OutputFormat.Mkv, ffmpeg.MovieWithChapters());
        var output = ffmpeg.NewPath(".mkv");
        await File.WriteAllTextAsync(output, "previous version");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var result = await Runner().RunAsync(plan with { OutputPath = output }, ct: cancellation.Token);

        result.Outcome.Should().Be(RemuxOutcome.Canceled);
        (await File.ReadAllTextAsync(output)).Should().Be("previous version");
        File.Exists(OutputPaths.PartialFile(output)).Should().BeFalse();
    }

    private RemuxJobRunner Runner() =>
        new(Builder, new ProcessRunner(TimeProvider.System, new TranscodingPreferences()), JobLogs(), TimeProvider.System);

    private JobLogStore JobLogs() => new(
        new JobLogOptions(Path.Combine(ffmpeg.Directory, "logs"), TimeSpan.FromDays(30), "FFRux tests"),
        TimeProvider.System,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<JobLogStore>.Instance);

    private Task<RemuxPlan> PlanAsync(OutputFormat format, params string[] paths) => BuildPlanAsync(format, paths, null);

    private Task<RemuxPlan> PlanWithCharsetAsync(OutputFormat format, string path, string charset) =>
        BuildPlanAsync(format, [path], charset);

    private async Task<RemuxPlan> BuildPlanAsync(OutputFormat format, string[] paths, string? charset)
    {
        var sources = new List<RemuxSource>();
        for (var i = 0; i < paths.Length; i++)
        {
            var info = await _probe.ProbeAsync(paths[i]);
            sources.Add(new RemuxSource { Number = i + 1, Path = paths[i], MediaInfo = info, SubtitleCharset = charset });
        }
        var tracks = sources
            .SelectMany(source => SourceTracks.Create(source.Number, source.MediaInfo))
            .OrderBy(track => track.Kind)
            .Select(track => new RemuxTrack(track, SourceTracks.InitialSettings(track, FileNameHint.None)))
            .ToList();
        return new RemuxPlan { Sources = sources, Tracks = tracks, Format = format };
    }

    private async Task<string> RemuxAsync(RemuxPlan plan)
    {
        var output = ffmpeg.NewPath(plan.FormatInfo.Extension);
        var command = Builder.Build(plan, output).Ffmpeg;
        var (exitCode, _, stderr) = await Task.Run(() => FFmpegFixture.Run(command.ExecutablePath, command.Arguments));
        exitCode.Should().Be(0, $"FFmpeg should accept {command.DisplayString}{Environment.NewLine}{stderr}");
        return output;
    }

    private async Task<string> SubtitleSourceAsync(string codec)
    {
        var srt = ffmpeg.TextFile("cue.srt", Encoding.UTF8.GetBytes("1\n00:00:00,200 --> 00:00:00,800\nHello\n\n"));
        if (codec == "subrip")
            return srt;
        var (extension, encoder) = codec switch
        {
            "ass" => (".ass", "ass"),
            "webvtt" => (".vtt", "webvtt"),
            "mov_text" => (".mp4", "mov_text"),
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
        };
        var output = ffmpeg.NewPath(extension);
        var (exitCode, _, stderr) = await Task.Run(() => FFmpegFixture.Run(ffmpeg.Paths.FfmpegPath,
            ["-nostdin", "-y", "-loglevel", "error", "-i", srt, "-c:s", encoder, output]));
        exitCode.Should().Be(0, stderr);
        return output;
    }

    private async Task<string> WebmMovieAsync()
    {
        var output = ffmpeg.NewPath(".webm");
        var (exitCode, _, stderr) = await Task.Run(() => FFmpegFixture.Run(ffmpeg.Paths.FfmpegPath,
            ["-nostdin", "-y", "-loglevel", "error", "-i", ffmpeg.MovieWithChapters(), "-c:v", "libvpx-vp9", "-b:v", "200k", "-c:a", "libopus", output]));
        exitCode.Should().Be(0, stderr);
        return output;
    }

    private List<JsonElement> Streams(string path) => [.. ffmpeg.Probe(path).GetProperty("streams").EnumerateArray()];

    private List<string> StreamCodecs(string path) =>
        [.. Streams(path).Select(stream => stream.GetProperty("codec_name").GetString() ?? "")];

    private List<double> PacketTimes(string path, string streamType)
    {
        var (_, stdout, _) = FFmpegFixture.Run(ffmpeg.Paths.FfprobePath,
            ["-v", "error", "-select_streams", streamType, "-show_entries", "packet=pts_time", "-of", "csv=p=0", path]);
        return [.. stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => double.Parse(line.TrimEnd(','), CultureInfo.InvariantCulture))
            .Order()];
    }
}

file static class PlanExtensions
{
    public static RemuxPlan WithAllTracks(this RemuxPlan plan, Func<TrackSettings, TrackSettings> change) =>
        plan with { Tracks = [.. plan.Tracks.Select(track => track with { Settings = change(track.Settings) })] };

    public static RemuxPlan WithTrack(this RemuxPlan plan, int sourceNumber, int streamIndex, Func<TrackSettings, TrackSettings> change) =>
        Builders.TestPlans.WithTrack(plan, sourceNumber, streamIndex, change);
}
