using FFApp.Core.Models;
using FFRux.Core;
using FFRux.Core.Models;
using FluentAssertions;
using static FFRux.Tests.Builders.TestPlans;

namespace FFRux.Tests.Core;

public sealed class CompatibilityCheckerTests
{
    [Fact]
    public void Matroska_CopiesEveryTrackOfAMovie()
    {
        var report = CompatibilityChecker.Check(Plan(OutputFormat.Mkv, Movie()));

        report.Tracks.Values.Should().OnlyContain(status => status.Level == CompatibilityLevel.Copy);
        report.Tracks.Values.Should().OnlyContain(status => status.StatusText == "Copy");
        report.CanStart.Should().BeTrue();
        report.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData(OutputFormat.Mp4, "subrip", "mov_text", "Convert to MP4 text", "Text and timing stay the same")]
    [InlineData(OutputFormat.Webm, "ass", "webvtt", "Convert to WebVTT", "Styling such as fonts")]
    [InlineData(OutputFormat.Mkv, "mov_text", "subrip", "Convert to SubRip", "Text and timing stay the same")]
    public void TextSubtitlesTheFormatCannotHold_AreConverted(OutputFormat format, string codec, string convertTo, string statusText, string detail)
    {
        var status = CompatibilityChecker.Check(Plan(format, Media("/media/a.mkv", subtitles: [Subtitle(0, codec)]))).Status(1, 0);

        status.Level.Should().Be(CompatibilityLevel.Convert);
        status.ConvertToCodec.Should().Be(convertTo);
        status.StatusText.Should().Be(statusText);
        status.Detail.Should().Contain(detail);
    }

    [Fact]
    public void Mp4_BlocksTrueHdAndSuggestsMatroska()
    {
        var media = Media("/media/a.mkv", [Video(0)], [Audio(1, "truehd")]);

        var report = CompatibilityChecker.Check(Plan(OutputFormat.Mp4, media));

        report.Status(1, 1).Level.Should().Be(CompatibilityLevel.Incompatible);
        report.Status(1, 1).StatusText.Should().Be("Not supported in MP4");
        report.CanStart.Should().BeFalse();
        report.Issues.Should().ContainSingle(issue => issue.Severity == IssueSeverity.Error)
            .Which.Message.Should().Be("Audio 1 (TrueHD, eng): MP4 can't hold TrueHD. Disable the track or choose MKV.");
    }

    [Fact]
    public void Mp4_WarnsAboutDtsButAllowsTheStart()
    {
        var media = Media("/media/a.mkv", [Video(0)], [Audio(1, "dts")]);

        var report = CompatibilityChecker.Check(Plan(OutputFormat.Mp4, media));

        report.Status(1, 1).Level.Should().Be(CompatibilityLevel.Warning);
        report.Status(1, 1).StatusText.Should().Be("Limited player support");
        report.CanStart.Should().BeTrue();
        report.WarningCount.Should().Be(1);
    }

    [Fact]
    public void Mp4_BlocksBitmapSubtitles()
    {
        var media = Media("/media/a.mkv", [Video(0)], [], [Subtitle(1, "hdmv_pgs_subtitle")]);

        var status = CompatibilityChecker.Check(Plan(OutputFormat.Mp4, media)).Status(1, 1);

        status.Level.Should().Be(CompatibilityLevel.Incompatible);
        status.StatusText.Should().Be("Bitmap subtitles need MKV");
    }

    [Fact]
    public void MpegTs_KeepsDvbSubtitlesButBlocksTextSubtitles()
    {
        var media = Media("/media/a.ts", [Video(0)], [], [Subtitle(1, "dvb_subtitle"), Subtitle(2, "subrip")], format: "mpegts");

        var report = CompatibilityChecker.Check(Plan(OutputFormat.Ts, media));

        report.Status(1, 1).Level.Should().Be(CompatibilityLevel.Copy);
        report.Status(1, 2).Level.Should().Be(CompatibilityLevel.Incompatible);
    }

    [Fact]
    public void AudioFormats_RejectVideo()
    {
        var media = Media("/media/a.mkv", [Video(0)], [Audio(1)]);

        var status = CompatibilityChecker.Check(Plan(OutputFormat.Mka, media)).Status(1, 0);

        status.Level.Should().Be(CompatibilityLevel.Incompatible);
        status.Detail.Should().Contain("audio format");
    }

    [Fact]
    public void M4a_RejectsSubtitlesButMatroskaAudioKeepsThem()
    {
        var media = Media("/media/a.mka", [], [Audio(0)], [Subtitle(1)]);

        CompatibilityChecker.Check(Plan(OutputFormat.M4a, media)).Status(1, 1).Level.Should().Be(CompatibilityLevel.Incompatible);
        CompatibilityChecker.Check(Plan(OutputFormat.Mka, media)).Status(1, 1).Level.Should().Be(CompatibilityLevel.Copy);
    }

    [Fact]
    public void Mp3_AcceptsExactlyOneAudioTrack()
    {
        var media = Media("/media/a.mka", [], [Audio(0, "mp3"), Audio(1, "mp3")]);

        var report = CompatibilityChecker.Check(Plan(OutputFormat.Mp3, media));

        report.Issues.Should().Contain(issue => issue.Severity == IssueSeverity.Error && issue.Message.Contains("exactly one audio track"));
        var single = CompatibilityChecker.Check(Plan(OutputFormat.Mp3, media).WithTrack(1, 1, s => s with { Enabled = false }));
        single.CanStart.Should().BeTrue();
    }

    [Fact]
    public void AudioFormat_WithOnlySubtitles_NeedsAnAudioTrack()
    {
        var media = Media("/media/a.mka", [], [Audio(0)], [Subtitle(1)]);
        var plan = Plan(OutputFormat.Mka, media).WithTrack(1, 0, s => s with { Enabled = false });

        CompatibilityChecker.Check(plan).Issues.Should().Contain(issue => issue.Message.Contains("needs an audio track"));
    }

    [Fact]
    public void NoEnabledTrack_BlocksTheStart()
    {
        var plan = Plan(OutputFormat.Mkv, Movie());
        plan = plan with { Tracks = [.. plan.Tracks.Select(t => t with { Settings = t.Settings with { Enabled = false } })] };

        var report = CompatibilityChecker.Check(plan);

        report.CanStart.Should().BeFalse();
        report.Issues.Should().ContainSingle().Which.Message.Should().Be("Enable at least one track.");
        report.Tracks.Should().BeEmpty();
    }

    [Fact]
    public void SeveralDefaultTracksOfOneType_ProduceAWarning()
    {
        var media = Media("/media/a.mkv", [Video(0)], [Audio(1), Audio(2, language: "ger")]);
        var plan = Plan(OutputFormat.Mkv, media)
            .WithTrack(1, 1, s => s with { Flags = TrackFlags.Default })
            .WithTrack(1, 2, s => s with { Flags = TrackFlags.Default });

        CompatibilityChecker.Check(plan).Issues.Should().ContainSingle()
            .Which.Message.Should().Be("2 audio tracks are marked as default. Players pick only one of them.");
    }

    [Fact]
    public void ChaptersInFlac_AreDroppedWithAWarning()
    {
        var media = Media("/media/a.mka", [], [Audio(0, "flac")]);
        var plan = Plan(OutputFormat.Flac, media) with { ChapterSourceNumber = 1 };

        var report = CompatibilityChecker.Check(plan);

        report.CanStart.Should().BeTrue();
        report.Issues.Should().ContainSingle().Which.Message.Should().Contain("can't store chapters");
    }

    [Fact]
    public void PositiveAudioShiftInMp4_WarnsAboutEditLists()
    {
        var plan = Plan(OutputFormat.Mp4, Movie()).WithTrack(1, 1, s => s with { TimeshiftMs = 200 });

        var status = CompatibilityChecker.Check(plan).Status(1, 1);

        status.Level.Should().Be(CompatibilityLevel.Warning);
        status.StatusText.Should().Be("Shift stored as edit list");
    }

    [Fact]
    public void QuickTime_WarnsWhenItCannotStoreTheLanguage()
    {
        var plan = Plan(OutputFormat.Mov, Movie()).WithTrack(1, 1, s => s with { Language = Languages.Find("haw")! });

        CompatibilityChecker.Check(plan).Status(1, 1).StatusText.Should().Be("Language not stored");
    }

    [Fact]
    public void FlagsTheFormatCannotStore_AreNamedInTheDetail()
    {
        var plan = Plan(OutputFormat.Webm, Media("/media/a.mkv", [Video(0, "vp9")], [Audio(1, "opus")]))
            .WithTrack(1, 1, s => s with { Flags = TrackFlags.Commentary | TrackFlags.Default });

        var status = CompatibilityChecker.Check(plan).Status(1, 1);

        status.Level.Should().Be(CompatibilityLevel.Copy);
        status.Detail.Should().Contain("left out: commentary");
    }

    [Fact]
    public void UnknownCodec_IsAWarningInMatroskaAndIncompatibleElsewhere()
    {
        var media = Media("/media/a.mkv", [Video(0, "cinepak")]);

        CompatibilityChecker.Check(Plan(OutputFormat.Mkv, media)).Status(1, 0).StatusText.Should().Be("Unchecked codec");
        CompatibilityChecker.Check(Plan(OutputFormat.Mp4, media)).Status(1, 0).Level.Should().Be(CompatibilityLevel.Incompatible);
    }

    [Fact]
    public void TrackLabel_NumbersTracksWithinTheirGroup()
    {
        var media = Media("/media/a.mkv", [Video(0)], [Audio(1, "ac3", "ger"), Audio(2, "dts", null)]);
        var plan = Plan(OutputFormat.Mkv, media);

        CompatibilityChecker.TrackLabel(plan, plan.Tracks[2]).Should().Be("Audio 2 (DTS)");
        CompatibilityChecker.TrackLabel(plan, plan.Tracks[1]).Should().Be("Audio 1 (AC-3, ger)");
    }

    [Fact]
    public void OutputPath_MustNotBeASource()
    {
        var plan = Plan(OutputFormat.Mkv, Movie("/media/movie.mkv")) with { OutputPath = "/media/movie.mkv" };

        CompatibilityChecker.CheckOutputPath(plan).Should().ContainSingle().Which.Severity.Should().Be(IssueSeverity.Error);
        CompatibilityChecker.CheckOutputPath(plan with { OutputPath = "/media/other.mkv" }).Should().BeEmpty();
        CompatibilityChecker.CheckOutputPath(plan with { OutputPath = null }).Should().ContainSingle();
    }

    [Fact]
    public void DolbyVision_IsKeptInMatroskaAndUncheckedInMpegTs()
    {
        var video = Video(0, "hevc") with { HasDolbyVision = true };
        var media = Media("/media/dv.mkv", [video]);

        CompatibilityChecker.Check(Plan(OutputFormat.Mkv, media)).Status(1, 0).Detail.Should().Contain("Dolby Vision metadata is copied");
        CompatibilityChecker.Check(Plan(OutputFormat.Ts, media)).Status(1, 0).Level.Should().Be(CompatibilityLevel.Warning);
    }
}
