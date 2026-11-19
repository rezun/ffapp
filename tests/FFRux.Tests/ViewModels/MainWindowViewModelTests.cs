using System.Text;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFRux.Core;
using FFRux.Core.Models;
using FFRux.Infrastructure;
using FFRux.Services;
using FFRux.ViewModels;
using FluentAssertions;
using NSubstitute;
using static FFRux.Tests.Builders.TestPlans;

namespace FFRux.Tests.ViewModels;

public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly IDependencyService _dependencies = Substitute.For<IDependencyService>();
    private readonly ExecutablePathsConfig _pathsConfig = new();
    private readonly IMediaProbeService _probe = Substitute.For<IMediaProbeService>();
    private readonly IDialogService _dialogs = Substitute.For<IDialogService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();
    private readonly IRemuxJobRunner _runner = Substitute.For<IRemuxJobRunner>();
    private readonly string _tempDir = Directory.CreateTempSubdirectory("ffrux-vm-").FullName;

    public MainWindowViewModelTests()
    {
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new FFRuxSettings());
        ToolsAvailable(true);
    }

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private MainWindowViewModel CreateSut() => new(
        _dependencies, _pathsConfig, _probe, _dialogs, _settings, _runner,
        new RemuxCommandBuilder(_pathsConfig),
        new OutputViewModel(_dialogs),
        new TrackPropertiesViewModel(),
        new CommandPreviewViewModel(_dialogs));

    private static DependencyStatus Status(bool ready, bool mkvpropedit = false) => new(
        new ExecutableValidation("ffmpeg", "/opt/bin/ffmpeg", ready),
        new ExecutableValidation("ffprobe", "/opt/bin/ffprobe", ready),
        new ExecutableValidation("mkvpropedit", mkvpropedit ? "/opt/bin/mkvpropedit" : null, mkvpropedit));

    private void ToolsAvailable(bool ready) =>
        _dependencies.DiscoverAsync(Arg.Any<ExecutablePaths>(), Arg.Any<CancellationToken>()).Returns(Status(ready));

    [Fact]
    public async Task InitializeAsync_UsesConfiguredPathsAndShowsTheSpecificFailure()
    {
        var paths = new ExecutablePaths("/custom/ffmpeg", "/custom/ffprobe");
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new FFRuxSettings { ExecutablePaths = paths });
        _dependencies.DiscoverAsync(paths, Arg.Any<CancellationToken>()).Returns(new DependencyStatus(
            new("ffmpeg", paths.Ffmpeg, true, "ffmpeg version 9.0.2"),
            new("ffprobe", paths.Ffprobe, false, Error: "ffprobe 9 or newer is required. Found 8.0."),
            new("mkvpropedit", null, false)));
        var sut = CreateSut();
        await sut.InitializeAsync();
        sut.FfmpegPath.Should().Be(paths.Ffmpeg);
        sut.ToolsMessage.Should().Contain("Found 8.0");
        sut.ToolDetails.Should().Contain("ffprobe 9 or newer is required");
        sut.IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task ToolRecheck_RecoversWithoutRestartAndPersistsPathsAlongsideOutputOptions()
    {
        ToolsAvailable(false);
        var sut = CreateSut();
        await sut.InitializeAsync();
        ToolsAvailable(true);
        sut.Output.UpdateStatistics = false;
        await sut.CheckToolsCommand.ExecuteAsync(null);
        sut.IsReady.Should().BeTrue();
        sut.ToolsMessage.Should().BeNull();
        await _settings.Received().SaveAsync(Arg.Is<FFRuxSettings>(saved => saved.ExecutablePaths.Ffmpeg == "/opt/bin/ffmpeg"
            && saved.UpdateMatroskaStatistics == false), Arg.Any<CancellationToken>());
    }

    private void Probes(params SourceMediaInfo[] files)
    {
        foreach (var file in files)
            _probe.ProbeAsync(file.SourcePath, Arg.Any<CancellationToken>()).Returns(file);
    }

    private async Task<MainWindowViewModel> OpenAsync(params SourceMediaInfo[] files)
    {
        Probes(files);
        var sut = CreateSut();
        await sut.OpenFilesAsync([.. files.Select(f => f.SourcePath)]);
        return sut;
    }

    [Fact]
    public async Task InitializeAsync_ConfiguresDiscoveredTools()
    {
        var sut = CreateSut();

        await sut.InitializeAsync();

        sut.IsReady.Should().BeTrue();
        _pathsConfig.FfprobePath.Should().Be("/opt/bin/ffprobe");
        sut.AddSourcesCommand.CanExecute(null).Should().BeTrue();
        sut.StatusMessage.Should().Be("Add source files to start.");
    }

    [Fact]
    public async Task InitializeAsync_WithoutTools_ExplainsWhatIsMissing()
    {
        ToolsAvailable(false);
        var sut = CreateSut();

        await sut.InitializeAsync();

        sut.IsReady.Should().BeFalse();
        sut.ToolsMessage.Should().Contain("ffprobe").And.Contain("Tools and components");
        sut.IsStatusError.Should().BeTrue();
        sut.AddSourcesCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task InitializeAsync_RestoresTheLastFormat()
    {
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new FFRuxSettings { LastFormat = OutputFormat.Mp4 });
        var sut = CreateSut();

        await sut.InitializeAsync();

        sut.Output.Format.Should().Be(OutputFormat.Mp4);
        sut.Output.OptimizeForStreaming.Should().BeFalse();
    }

    [Fact]
    public async Task StreamingOptimization_RestoresAndSavesThePreferenceAndUpdatesTheCommandPreview()
    {
        _settings.LoadAsync(Arg.Any<CancellationToken>()).Returns(new FFRuxSettings
        {
            LastFormat = OutputFormat.Mkv,
            OptimizeForStreaming = true,
        });
        var sut = await OpenAsync(Movie("/media/movie.mkv"));

        sut.CommandPreview.CommandText.Should().Contain("-cues_to_front 1");

        sut.Output.OptimizeForStreaming = false;

        sut.CommandPreview.CommandText.Should().NotContain("-cues_to_front");
        await _settings.Received().SaveAsync(Arg.Is<FFRuxSettings>(saved =>
            saved.LastFormat == OutputFormat.Mkv && saved.OptimizeForStreaming == false), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InitializeAsync_DiscoversOnlyOnce()
    {
        var sut = CreateSut();

        await Task.WhenAll(sut.InitializeAsync(), sut.InitializeAsync());
        await sut.OpenFilesAsync([]);

        await _dependencies.Received(1).DiscoverAsync(Arg.Any<ExecutablePaths>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OpenFilesAsync_GroupsTracksByTypeAcrossSources()
    {
        var sut = await OpenAsync(Movie("/media/movie.mkv"), Media("/media/dub.ac3", audio: [Audio(0, "ac3", "ger")], format: "ac3"));

        sut.Sources.Select(s => s.Number).Should().Equal(1, 2);
        sut.Rows.Select(Describe).Should().Equal(
            "Video", "1:H.264", "Audio", "1:AAC", "2:AC-3", "Subtitles", "1:SubRip");
        sut.TracksSummary.Should().StartWith("4 of 4 enabled");
        sut.CanStart.Should().BeTrue();
        sut.Output.OutputPath.Should().Be("/media/movie (remux).mkv");
    }

    [Fact]
    public async Task ToggleTracksCommand_ExcludesAllOfOneSourceAndIncludesAllWhenAnyIsExcluded()
    {
        var sut = await OpenAsync(Movie("/media/movie.mkv"), Media("/media/dub.ac3", audio: [Audio(0, "ac3", "ger")], format: "ac3"));
        var movie = sut.Sources[0];

        movie.ToggleTracksCommand.Execute(null);

        movie.TracksEnabled.Should().BeFalse();
        sut.Tracks.Where(t => t.SourceFile == movie).Should().OnlyContain(t => !t.Enabled);
        sut.Sources[1].TracksEnabled.Should().BeTrue();
        sut.TracksSummary.Should().StartWith("1 of 4 enabled");

        sut.Tracks.First(t => t.SourceFile == movie).Enabled = true;
        movie.TracksEnabled.Should().BeNull();
        movie.ToggleTracksCommand.Execute(null);

        movie.TracksEnabled.Should().BeTrue();
        sut.TracksSummary.Should().StartWith("4 of 4 enabled");
    }

    [Fact]
    public async Task ToggleSelectedTracksCommand_TogglesTheSelection()
    {
        var sut = await OpenAsync(Movie("/media/movie.mkv"));
        var audio = sut.Tracks.Single(t => t.Kind == TrackKind.Audio);
        var subtitle = sut.Tracks.Single(t => t.Kind == TrackKind.Subtitle);
        sut.ToggleSelectedTracksCommand.CanExecute(null).Should().BeFalse();

        sut.SelectTracks([audio, subtitle]);
        sut.ToggleSelectedTracksCommand.Execute(null);

        audio.Enabled.Should().BeFalse();
        subtitle.Enabled.Should().BeFalse();
        sut.Tracks.Single(t => t.Kind == TrackKind.Video).Enabled.Should().BeTrue();

        subtitle.Enabled = true;
        sut.ToggleSelectedTracksCommand.Execute(null);

        audio.Enabled.Should().BeTrue();
        subtitle.Enabled.Should().BeTrue();
    }

    private static string Describe(object row) => row switch
    {
        TrackGroupViewModel group => group.Label,
        TrackViewModel track => $"{track.SourceNumber}:{track.CodecLabel}",
        _ => "?",
    };

    [Fact]
    public async Task OpenFilesAsync_SkipsFilesThatAreAlreadyAdded()
    {
        var sut = await OpenAsync(Movie("/media/movie.mkv"));

        await sut.OpenFilesAsync(["/media/movie.mkv"]);

        sut.Sources.Should().ContainSingle();
    }

    [Fact]
    public async Task OpenFilesAsync_ProbeFailure_KeepsTheFileWithAnError()
    {
        _probe.ProbeAsync("/media/broken.mkv", Arg.Any<CancellationToken>())
            .Returns<SourceMediaInfo>(_ => throw new MediaProbeException("boom"));
        var sut = await OpenAsync(Movie("/media/good.mkv"));

        await sut.OpenFilesAsync(["/media/broken.mkv"]);

        sut.Sources[1].IsError.Should().BeTrue();
        sut.Sources[1].Summary.Should().Be("boom");
        sut.Issues.Should().Contain(issue => issue.Message.Contains("can't be read"));
        sut.Tracks.Should().HaveCount(3);
    }

    [Fact]
    public async Task OpenFilesAsync_PrefillsLanguageAndFlagsFromTheFileName()
    {
        var srt = Media("/media/Movie.de.forced.srt", subtitles: [Subtitle(0, "subrip", null)], format: "srt");

        var sut = await OpenAsync(Movie("/media/Movie.mkv"), srt);

        // The rule itself is covered in SourceTracksTests; this checks the hint reaches the row.
        var track = sut.Tracks.Last();
        track.LanguageCode.Should().Be("ger");
        track.PrefillText.Should().NotBeNull();
    }

    [Fact]
    public async Task OpenFilesAsync_DetectsSubtitleFilesThatAreNotUtf8()
    {
        var path = Path.Combine(_tempDir, "Movie.de.srt");
        await File.WriteAllBytesAsync(path, [.. Encoding.ASCII.GetBytes("1\n00:00:01,000 --> 00:00:02,000\nGr"), 0xFC, 0xDF, 0x65, (byte)'\n']);
        var srt = Media(path, subtitles: [Subtitle(0, "subrip", null)], format: "srt");

        var sut = await OpenAsync(srt);

        sut.Sources[0].IsStandaloneTextSubtitle.Should().BeTrue();
        sut.Sources[0].Encoding.Should().Be(TextEncodings.Fallback);
        sut.Tracks.Single().StatusText.Should().Be("Convert to UTF-8");
    }

    [Fact]
    public async Task Chapters_DefaultToTheFirstSourceWithChapters()
    {
        var withChapters = Movie("/media/b.mkv") with { Chapters = [new ChapterInfo(0, 10, "Intro")] };

        var sut = await OpenAsync(Movie("/media/a.mkv"), withChapters);

        sut.ChapterOptions.Select(o => o.Label).Should().Equal("1 chapter", "No chapters");
        sut.SelectedChapter!.SourceNumber.Should().Be(2);
        sut.CommandPreview.CommandText.Should().Contain("-map_chapters 1");
    }

    [Fact]
    public async Task ChangingTheFormat_UpdatesStatusesAndProblemCounts()
    {
        var sut = await OpenAsync(Media("/media/a.mkv", [Video(0)], [Audio(1, "truehd")], [Subtitle(2, "hdmv_pgs_subtitle")]));

        sut.Output.SelectFormat(OutputFormat.Mp4);

        sut.StatusColumnHeader.Should().Be("Status for MP4");
        sut.Tracks.Count(t => t.IsStatusError).Should().Be(2);
        sut.CanStart.Should().BeFalse();
        sut.StartCommand.CanExecute(null).Should().BeFalse();
        sut.StatusMessage.Should().Be("✕ 2 tracks block the start");
        sut.Output.Formats.Single(f => f.Format == OutputFormat.Mp4).ProblemText.Should().Be("2 problems");
        sut.Output.Formats.Single(f => f.Format == OutputFormat.Mkv).ProblemText.Should().BeEmpty();
        sut.Output.OutputPath.Should().Be("/media/a.mp4");
    }

    [Fact]
    public async Task DisablingABlockingTrack_AllowsTheStart()
    {
        var sut = await OpenAsync(Media("/media/a.mkv", [Video(0)], [Audio(1, "truehd")]));
        sut.Output.SelectFormat(OutputFormat.Mp4);

        sut.Tracks.Single(t => t.Kind == TrackKind.Audio).Enabled = false;

        sut.CanStart.Should().BeTrue();
        sut.Tracks.Single(t => t.Kind == TrackKind.Audio).StatusText.Should().Be("Not included");
    }

    [Fact]
    public async Task RemoveSource_RemovesItsTracksAndRenumbers()
    {
        var sut = await OpenAsync(Movie("/media/a.mkv"), Movie("/media/b.mkv"), Movie("/media/c.mkv"));

        sut.SelectedSource = sut.Sources[1];
        sut.RemoveSourceCommand.Execute(null);

        sut.Sources.Select(s => (s.FileName, s.Number)).Should().Equal(("a.mkv", 1), ("c.mkv", 2));
        sut.Tracks.Select(t => t.SourceNumber).Distinct().Should().Equal(1, 2);
        sut.Tracks.Should().HaveCount(6);
    }

    [Fact]
    public async Task MoveTrack_ReordersWithinTheGroupOnly()
    {
        var sut = await OpenAsync(Media("/media/a.mkv", [Video(0)], [Audio(1, "aac"), Audio(2, "ac3")], [Subtitle(3)]));
        var audio = sut.Tracks.Where(t => t.Kind == TrackKind.Audio).ToList();

        sut.MoveTrack(audio[1], audio[0]);
        sut.MoveTrack(audio[0], sut.Tracks.First());

        sut.Tracks.Where(t => t.Kind == TrackKind.Audio).Select(t => t.CodecLabel).Should().Equal("AC-3", "AAC");
        sut.Rows.Select(Describe).Should().Equal("Video", "1:H.264", "Audio", "1:AC-3", "1:AAC", "Subtitles", "1:SubRip");
        sut.CommandPreview.CommandText.Should().Contain("-map 0:0 -map 0:2 -map 0:1 -map 0:3");
    }

    [Fact]
    public async Task Properties_EditSeveralTracksTogether()
    {
        var sut = await OpenAsync(Media("/media/a.mkv", [], [Audio(0, language: "eng"), Audio(1, language: "ger")]));
        var tracks = sut.Tracks.ToList();

        sut.SelectTracks(tracks);

        sut.Properties.Language.Should().BeNull();
        sut.Properties.LanguageWatermark.Should().Be(TrackPropertiesViewModel.MultipleValues);
        sut.Properties.IsDefault.Should().BeFalse();

        sut.Properties.IsCommentary = true;
        sut.Properties.Name = "Commentary";

        tracks.Should().OnlyContain(t => t.HasFlag(TrackFlags.Commentary) && t.Name == "Commentary");
        tracks.Select(t => t.LanguageCode).Should().Equal("eng", "ger");
    }

    [Fact]
    public async Task Properties_DisableWhatTheFormatOrTrackCannotStore()
    {
        var sut = await OpenAsync(Movie());
        sut.SelectTracks([sut.Tracks.First()]);

        sut.Output.SelectFormat(OutputFormat.Mov);

        sut.Properties.DefaultSupported.Should().BeTrue();
        sut.Properties.ForcedSupported.Should().BeFalse();
        sut.Properties.FlagsHint.Should().Be("Greyed flags can't be stored in MOV.");
        sut.Properties.TimeshiftEnabled.Should().BeFalse("video tracks can't be shifted");
    }

    [Fact]
    public async Task Start_RunsTheJobAndShowsTheResult()
    {
        var output = Path.Combine(_tempDir, "out.mkv");
        _runner.RunAsync(Arg.Any<RemuxPlan>(), Arg.Any<Action<RemuxProgress>?>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>())
            .Returns(new RemuxResult(RemuxOutcome.Completed, output, TimeSpan.FromSeconds(42), OutputBytes: 2048));
        var sut = await OpenAsync(Movie());
        sut.Output.OutputPath = output;

        await sut.StartCommand.ExecuteAsync(null);

        await _runner.Received(1).RunAsync(Arg.Is<RemuxPlan>(p => p.OutputPath == output && p.EnabledTracks.Count == 3),
            Arg.Any<Action<RemuxProgress>?>(), Arg.Any<Action<string>?>(), Arg.Any<CancellationToken>());
        sut.HasResult.Should().BeTrue();
        sut.ResultSucceeded.Should().BeTrue();
        sut.ResultText.Should().Be("✓ Finished in 0:42 · out.mkv · 2 KB");
        sut.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Start_AsksBeforeReplacingAFile()
    {
        var output = Path.Combine(_tempDir, "exists.mkv");
        await File.WriteAllTextAsync(output, "old");
        _dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        var sut = await OpenAsync(Movie());
        sut.Output.OutputPath = output;

        await sut.StartCommand.ExecuteAsync(null);

        await _dialogs.Received(1).ConfirmAsync("Replace file?", Arg.Any<string>(), "Replace");
        await _runner.DidNotReceiveWithAnyArgs().RunAsync(default!, default, default, default);
    }

    [Fact]
    public async Task Output_TitleStartsWithTheFirstSourceTitle()
    {
        var sut = await OpenAsync(Movie("/media/a.mkv"), Movie("/media/b.mkv") with { Title = "Interstellar" });

        sut.Output.Title.Should().Be("Interstellar");
    }
}
