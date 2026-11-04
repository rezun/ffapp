using System.Collections.Immutable;
using FFApp.Core;
using FFApp.Core.Models;
using FFRux.Core;
using FFRux.Core.Models;
using FluentAssertions;
using static FFRux.Tests.Builders.TestPlans;

namespace FFRux.Tests.Core;

public sealed class RemuxCommandBuilderTests
{
    private readonly ExecutablePathsConfig _paths = new();
    private RemuxCommandBuilder Sut => new(_paths);

    private static string Value(ImmutableArray<string> args, string option)
    {
        var index = args.IndexOf(option);
        index.Should().BeGreaterThanOrEqualTo(0, $"{option} should be present");
        return args[index + 1];
    }

    private static List<string> Values(ImmutableArray<string> args, string option) =>
        [.. args.Select((arg, i) => (arg, i)).Where(p => p.arg == option).Select(p => args[p.i + 1])];

    [Fact]
    public void Build_MapsTracksInGroupOrderAcrossSources()
    {
        var movie = Source(1, Movie("/media/movie.mkv"));
        var dub = Source(2, Media("/media/dub.ac3", audio: [Audio(0, "ac3", "ger")], format: "ac3"));
        var plan = Plan(OutputFormat.Mkv, movie, dub);

        var args = Sut.Build(plan, "/out/result.mkv").Args();

        Values(args, "-i").Should().Equal("/media/movie.mkv", "/media/dub.ac3");
        Values(args, "-map").Should().Equal("0:0", "0:1", "1:0", "0:2");
        Value(args, "-c").Should().Be("copy");
        Value(args, "-f").Should().Be("matroska");
        args[^1].Should().Be("/out/result.mkv");
    }

    [Fact]
    public void Build_OpensASourceOncePerTimeshift()
    {
        var media = Media("/media/movie.mkv", [Video(0)], [Audio(1), Audio(2, language: "ger")], [Subtitle(3)]);
        var plan = Plan(OutputFormat.Mkv, media)
            .WithTrack(1, 2, s => s with { TimeshiftMs = 250 })
            .WithTrack(1, 3, s => s with { TimeshiftMs = -1500 });

        var args = Sut.Build(plan, "/out/result.mkv").Args();

        Values(args, "-i").Should().Equal("/media/movie.mkv", "/media/movie.mkv", "/media/movie.mkv");
        Value(args, "-itsoffset").Should().Be("0.25");
        Value(args, "-ss").Should().Be("1.5");
        Values(args, "-map").Should().Equal("0:0", "0:1", "1:2", "2:3");
        Value(args, "-copypriorss:3").Should().Be("0");
        args[args.IndexOf("-itsoffset") + 2].Should().Be("-i", "the offset applies to the input that follows it");
    }

    [Fact]
    public void Build_NeverShiftsVideo()
    {
        var plan = Plan(OutputFormat.Mkv, Movie()).WithTrack(1, 0, s => s with { TimeshiftMs = 500 });

        Sut.Build(plan, "/out/result.mkv").Args().Should().NotContain("-itsoffset");
    }

    [Fact]
    public void Build_WritesNameLanguageAndFlagsPerOutputTrack()
    {
        var plan = Plan(OutputFormat.Mkv, Movie())
            .WithTrack(1, 2, s => s with { Name = "Forced", Language = Languages.Find("ger")!, Flags = TrackFlags.Forced | TrackFlags.HearingImpaired });

        var args = Sut.Build(plan, "/out/result.mkv").Args();

        Values(args, "-metadata:s:2").Should().Contain(["title=Forced", "language=ger"]);
        Value(args, "-disposition:2").Should().Be("forced+hearing_impaired");
        Value(args, "-disposition:0").Should().Be("0");
    }

    [Fact]
    public void Build_Mp4_StoresTheNameAsHandlerNameAndDropsUnsupportedFlags()
    {
        var plan = Plan(OutputFormat.Mp4, Movie())
            .WithTrack(1, 1, s => s with { Name = "Stereo", Flags = TrackFlags.Default | TrackFlags.Original });

        var args = Sut.Build(plan, "/out/result.mp4").Args();

        Values(args, "-metadata:s:1").Should().Contain("handler_name=Stereo").And.NotContain(v => v.StartsWith("title=", StringComparison.Ordinal));
        Value(args, "-disposition:1").Should().Be("default");
        Values(args, "-movflags").Should().Equal("+faststart");
    }

    [Fact]
    public void Build_TagsHevcAsHvc1ForApplePlayers()
    {
        var media = Media("/media/a.mkv", [Video(0, "hevc")]);

        Value(Sut.Build(Plan(OutputFormat.Mp4, media), "/out/a.mp4").Args(), "-tag:0").Should().Be("hvc1");
        Sut.Build(Plan(OutputFormat.Mkv, media), "/out/a.mkv").Args().Should().NotContain("-tag:0");
    }

    [Fact]
    public void Build_TakesChaptersFromTheChosenSourceOrRemovesThem()
    {
        var chapters = Source(2, Media("/media/chapters.mkv", [Video(0)]));
        var plan = Plan(OutputFormat.Mkv, Source(1, Movie()), chapters)
            .WithTrack(2, 0, s => s with { Enabled = false }) with { ChapterSourceNumber = 2 };

        var args = Sut.Build(plan, "/out/a.mkv").Args();

        Values(args, "-i").Should().Equal("/media/movie.mkv", "/media/chapters.mkv");
        Value(args, "-map_chapters").Should().Be("1");
        Value(Sut.Build(plan with { ChapterSourceNumber = null }, "/out/a.mkv").Args(), "-map_chapters").Should().Be("-1", "FFmpeg copies chapters unless told not to");
    }

    [Fact]
    public void Build_SetsTheTitleExceptForMpegTs()
    {
        var plan = Plan(OutputFormat.Mkv, Movie()) with { Title = "Interstellar" };

        Value(Sut.Build(plan, "/out/a.mkv").Args(), "-metadata").Should().Be("title=Interstellar");
        Sut.Build(plan with { Format = OutputFormat.Ts }, "/out/a.ts").Args().Should().NotContain("title=Interstellar");
    }

    [Fact]
    public void Build_PassesTheCharacterSetOfASubtitleFile()
    {
        var srt = Source(2, Media("/media/movie.de.srt", subtitles: [Subtitle(0, "subrip", null)], format: "srt"), charset: "CP1252");
        var plan = Plan(OutputFormat.Mkv, Source(1, Movie()), srt);

        var args = Sut.Build(plan, "/out/a.mkv").Args();

        Values(args, "-sub_charenc").Should().Equal("CP1252");
        args.Skip(args.IndexOf("-sub_charenc")).SkipWhile(arg => arg != "-i").ElementAt(1)
            .Should().Be("/media/movie.de.srt", "the character set applies to the input that follows it");
        Value(args, "-c:3").Should().Be("subrip");
    }

    [Fact]
    public void Build_CopiesFontsForAssIntoMatroskaOnly()
    {
        var media = Media("/media/anime.mkv", [Video(0)], [], [Subtitle(1, "ass")]) with
        {
            DataStreams = [new DataStreamInfo(2, "ttf") { IsAttachment = true, FileName = "font.ttf", MimeType = "font/ttf" },
                new DataStreamInfo(3, "png") { IsAttachment = true, FileName = "cover.png", MimeType = "image/png" }],
        };

        Values(Sut.Build(Plan(OutputFormat.Mkv, media), "/out/a.mkv").Args(), "-map").Should().Equal("0:0", "0:1", "0:2");
        Values(Sut.Build(Plan(OutputFormat.Webm, media), "/out/a.webm").Args(), "-map").Should().Equal("0:0", "0:1");
    }

    [Fact]
    public void Build_ClearsStaleStatisticsTagsForMatroska()
    {
        var video = Video(0) with
        {
            Details = new StreamDetails { Tags = ImmutableDictionary<string, string>.Empty.Add("BPS-eng", "1").Add("DURATION", "x").Add("ENCODER", "y") },
        };
        var plan = Plan(OutputFormat.Mkv, Media("/media/a.mkv", [video]));

        Values(Sut.Build(plan, "/out/a.mkv").Args(), "-metadata:s:0").Should().Contain(["BPS-eng=", "DURATION="]).And.NotContain("ENCODER=");
    }

    [Fact]
    public void Build_PostProcessesMatroskaOnlyWhenMkvpropeditExists()
    {
        Sut.Build(Plan(OutputFormat.Mkv, Movie()), "/out/a.mkv").PostProcess.Should().BeNull();

        _paths.Configure(new ExecutablePaths(Ffmpeg: "/bin/ffmpeg", Ffprobe: "/bin/ffprobe", Mkvpropedit: "/bin/mkvpropedit"));
        var commands = Sut.Build(Plan(OutputFormat.Mkv, Movie()), "/out/a.mkv");

        commands.PostProcess!.Arguments.Should().Equal("/out/a.mkv", "--delete-track-statistics-tags", "--add-track-statistics-tags");
        commands.PostProcess.Protocol.Should().Be(ProcessProtocol.Plain);
        Sut.Build(Plan(OutputFormat.Mp4, Movie()), "/out/a.mp4").PostProcess.Should().BeNull();
        Sut.Build(Plan(OutputFormat.Mkv, Movie()) with { UpdateMatroskaStatistics = false }, "/out/a.mkv")
            .PostProcess.Should().BeNull();
    }

    [Fact]
    public void ExpectedDuration_AddsPositiveShifts()
    {
        var plan = Plan(OutputFormat.Mkv, Movie()).WithTrack(1, 1, s => s with { TimeshiftMs = 2000 });

        RemuxCommandBuilder.ExpectedDurationSeconds(plan).Should().Be(62);
    }
}
