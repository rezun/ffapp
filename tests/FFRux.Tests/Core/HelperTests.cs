using System.Collections.Immutable;
using System.Text;
using FFApp.Core.Models;
using FFRux.Core;
using FFRux.Core.Models;
using FluentAssertions;
using static FFRux.Tests.Builders.TestPlans;

namespace FFRux.Tests.Core;

public sealed class FileNameHintsTests
{
    [Theory]
    [InlineData("/m/Movie.de.forced.srt", "ger", TrackFlags.Forced)]
    [InlineData("/m/Movie.ger.forced.srt", "ger", TrackFlags.Forced)]
    [InlineData("/m/Movie.en.sdh.srt", "eng", TrackFlags.HearingImpaired)]
    [InlineData("/m/Movie.en.hi.srt", "eng", TrackFlags.HearingImpaired)]
    [InlineData("/m/Movie.hi.srt", "hin", TrackFlags.None)]
    [InlineData("/m/Interstellar.commentary.ac3", null, TrackFlags.Commentary)]
    [InlineData("/m/The.Movie.2020.fra.srt", "fre", TrackFlags.None)]
    public void Parse_ReadsLanguageAndFlagsFromTheEnd(string path, string? language, TrackFlags flags)
    {
        var hint = FileNameHints.Parse(path);

        hint.Language?.Code.Should().Be(language);
        if (language is null)
            hint.Language.Should().BeNull();
        hint.Flags.Should().Be(flags);
    }

    [Theory]
    [InlineData("/m/Movie.srt")]
    [InlineData("/m/de.srt")]
    [InlineData("/m/Movie.2020.srt")]
    public void Parse_IgnoresNamesWithoutKnownSuffixes(string path) =>
        FileNameHints.Parse(path).IsEmpty.Should().BeTrue();
}

public sealed class LanguagesTests
{
    [Theory]
    [InlineData("ger", "German")]
    [InlineData("deu", "German")]
    [InlineData("de", "German")]
    [InlineData("FRA", "French")]
    public void Find_AcceptsBibliographicTerminologyAndTwoLetterCodes(string code, string name) =>
        Languages.Find(code)!.Name.Should().Be(name);

    [Fact]
    public void FromFileValue_KeepsUnknownCodes()
    {
        Languages.FromFileValue("qaa").Code.Should().Be("qaa");
        Languages.FromFileValue(null).Should().Be(Languages.Undetermined);
        Languages.FromFileValue("deu").Code.Should().Be("ger");
    }

    [Fact]
    public void All_HasUniqueCodes() =>
        Languages.All.Select(language => language.Code).Should().OnlyHaveUniqueItems();

    [Fact]
    public void QuickTime_UsesTerminologyCodesWhereFFmpegExpectsThem()
    {
        QuickTimeLanguages.Map(Languages.Find("cze")!).Should().Be("ces");
        QuickTimeLanguages.Map(Languages.Find("ger")!).Should().Be("ger");
        QuickTimeLanguages.Map(Languages.Find("haw")!).Should().BeNull();
    }
}

public sealed class TextEncodingsTests
{
    [Fact]
    public void IsUnicode_AcceptsUtf8WithAndWithoutBomAndUtf16()
    {
        TextEncodings.IsUnicode(Encoding.UTF8.GetBytes("Grüße")).Should().BeTrue();
        TextEncodings.IsUnicode([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Grüße")]).Should().BeTrue();
        TextEncodings.IsUnicode([0xFF, 0xFE, 0x47, 0x00]).Should().BeTrue();
    }

    [Fact]
    public void IsUnicode_RejectsWindows1252()
    {
        // "Grüße" in Windows-1252: ü = 0xFC, ß = 0xDF.
        TextEncodings.IsUnicode([0x47, 0x72, 0xFC, 0xDF, 0x65]).Should().BeFalse();
    }
}

public sealed class OutputPathsTests
{
    private static readonly string Dir = Path.Combine(Path.GetTempPath(), "media");

    [Fact]
    public void Suggest_UsesTheFirstSourceWithTheNewExtension()
    {
        var source = Path.Combine(Dir, "Movie.mkv");

        OutputPaths.Suggest(source, OutputFormat.Mp4, [source]).Should().Be(Path.Combine(Dir, "Movie.mp4"));
    }

    [Fact]
    public void Suggest_NeverPointsAtASource()
    {
        var source = Path.Combine(Dir, "Movie.mkv");

        OutputPaths.Suggest(source, OutputFormat.Mkv, [source]).Should().Be(Path.Combine(Dir, "Movie (remux).mkv"));
    }

    [Fact]
    public void WithFormat_KeepsTheChosenNameAndSwapsTheExtension()
    {
        var chosen = Path.Combine(Dir, "Final cut.mkv");

        OutputPaths.WithFormat(chosen, OutputFormat.Webm, []).Should().Be(Path.Combine(Dir, "Final cut.webm"));
    }

    [Fact]
    public void PartialFile_IsHiddenNextToTheOutput() =>
        OutputPaths.PartialFile(Path.Combine(Dir, "Movie.mkv")).Should().Be(Path.Combine(Dir, ".Movie.ffrux-partial.mkv"));
}

public sealed class SizeEstimatorTests
{
    [Fact]
    public void Estimate_UsesMatroskaStatisticsTags()
    {
        var video = Video(0) with
        {
            Details = new StreamDetails { Tags = ImmutableDictionary<string, string>.Empty.Add("NUMBER_OF_BYTES-eng", "1000") },
        };
        var plan = Plan(OutputFormat.Mkv, Media("/m/a.mkv", [video], [Audio(1) with { BitRate = 8000 }]));

        SizeEstimator.Estimate(plan).Should().Be(new SizeEstimate(1000 + 60_000, IsComplete: true));
    }

    [Fact]
    public void Estimate_UsesTheFileSizeWhenAllTracksOfAFileAreKept()
    {
        var media = Movie() with { SizeBytes = 5000 };

        SizeEstimator.Estimate(Plan(OutputFormat.Mkv, media)).Should().Be(new SizeEstimate(5000, IsComplete: true));
    }

    [Fact]
    public void Estimate_IsIncompleteWhenADroppedTrackHidesTheRest()
    {
        var media = Movie() with { SizeBytes = 5000 };
        var plan = Plan(OutputFormat.Mkv, media).WithTrack(1, 2, s => s with { Enabled = false });

        SizeEstimator.Estimate(plan).IsComplete.Should().BeFalse();
    }
}

public sealed class SourceTracksTests
{
    [Fact]
    public void InitialSettings_TakeFileValuesAndFillGapsFromTheFileName()
    {
        var track = SourceTracks.Create(1, Media("/m/Movie.de.forced.srt", subtitles: [Subtitle(0, "subrip", null)]))[0];

        var settings = SourceTracks.InitialSettings(track, FileNameHints.Parse("/m/Movie.de.forced.srt"));

        settings.Language.Code.Should().Be("ger");
        settings.Flags.Should().Be(TrackFlags.Forced);
        SourceTracks.DescribePrefill(track, FileNameHints.Parse("/m/Movie.de.forced.srt")).Should().Be("Language, forced (from file name)");
    }

    [Fact]
    public void InitialSettings_KeepTheLanguageFromTheFile()
    {
        var track = SourceTracks.Create(1, Media("/m/a.mka", audio: [Audio(0, language: "eng")]))[0];

        SourceTracks.InitialSettings(track, FileNameHints.Parse("/m/a.de.mka")).Language.Code.Should().Be("eng");
    }

    [Fact]
    public void InitialSettings_MapsDispositions()
    {
        var disposition = new StreamDisposition { Default = true, Comment = true, Original = true };
        var track = SourceTracks.Create(1, Media("/m/a.mka", audio: [Audio(0, disposition: disposition)]))[0];

        SourceTracks.InitialSettings(track, FileNameHint.None).Flags
            .Should().Be(TrackFlags.Default | TrackFlags.Commentary | TrackFlags.Original);
    }

    [Theory]
    [InlineData("SoundHandler", null)]
    [InlineData("Core Media Audio", null)]
    [InlineData("Director's commentary", "Director's commentary")]
    public void TrackName_IgnoresWriterPlaceholders(string handler, string? expected)
    {
        var details = new StreamDetails { Tags = ImmutableDictionary<string, string>.Empty.Add("handler_name", handler) };

        SourceTracks.TrackName(details).Should().Be(expected);
    }

    [Fact]
    public void Describe_SummarizesVideoAndAudio()
    {
        var video = SourceTracks.Create(1, Media("/m/a.mkv", [Video(0) with { Height = 2160, ColorTransfer = "smpte2084", ColorPrimaries = "bt2020" }]))[0];
        var audio = SourceTracks.Create(1, Media("/m/a.mkv", audio: [Audio(0, "eac3") with { ChannelLayout = "5.1(side)", BitRate = 768_000 }]))[0];

        SourceTracks.Describe(video).Should().Be("2160p · 23.976 · HDR10");
        SourceTracks.Describe(audio).Should().Be("5.1 · 768 kb/s");
    }

    [Fact]
    public void Disposition_ListsFlagsInFFmpegSyntax()
    {
        TrackFlagNames.ToDisposition(TrackFlags.None).Should().Be("0");
        TrackFlagNames.ToDisposition(TrackFlags.Default | TrackFlags.Commentary).Should().Be("default+comment");
    }
}
