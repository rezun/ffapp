using System.Collections.Immutable;
using FFApp.Core.Models;
using FFApp.Helpers;
using FFApp.Tests.Builders;
using FluentAssertions;

namespace FFApp.Tests.Helpers;

public class FormatHelpersTests
{

    [Fact]
    public void BuildJobDefinition_PassesAudioSelectionFromContext()
    {
        var context = new JobContext(new TestMediaInfoBuilder().Build(), new OutputSettings(), SelectedAudioStreamIndices: [1, 5]);
        var job = FormatHelpers.BuildJobDefinition(context);
        job.SelectedAudioStreamIndices.GetValueOrDefault().Should().Equal(1, 5);
        FormatHelpers.BuildJobDefinition(context with { SelectedAudioStreamIndices = null })
            .SelectedAudioStreamIndices.Should().BeNull();
    }

    // --- BuildMediaSummaryText ---

    [Fact]
    public void BuildMediaSummaryText_WithFullInfo_ContainsAllSections()
    {
        var media = new TestMediaInfoBuilder().Build();
        var text = FormatHelpers.BuildMediaSummaryText(media);

        text.Should().Contain("Source:");
        text.Should().Contain("Format:");
        text.Should().Contain("Duration:");
        text.Should().Contain("Primary video stream");
        text.Should().Contain("Audio streams detail");
    }

    [Fact]
    public void BuildMediaSummaryText_NoPrimaryVideo_SkipsVideoSection()
    {
        var media = new TestMediaInfoBuilder().WithNoVideo().Build();
        var text = FormatHelpers.BuildMediaSummaryText(media);

        text.Should().NotContain("Primary video stream");
    }

    [Fact]
    public void BuildMediaSummaryText_MultipleVideoStreams_IncludesWarning()
    {
        var media = new TestMediaInfoBuilder().WithMultipleVideoStreams().Build();
        var text = FormatHelpers.BuildMediaSummaryText(media);

        text.Should().Contain("Warning: multiple video streams");
    }

    [Fact]
    public void BuildMediaSummaryText_NoAudio_SkipsAudioDetail()
    {
        var media = new TestMediaInfoBuilder().WithNoAudio().Build();
        var text = FormatHelpers.BuildMediaSummaryText(media);

        text.Should().NotContain("Audio streams detail");
    }

    // --- FormatValidationResult ---

    [Fact]
    public void FormatValidationResult_NoIssues_ReturnsValidMessage()
    {
        var result = new ValidationResult();
        FormatHelpers.FormatValidationResult(result).Should().Contain("valid");
    }

    [Fact]
    public void FormatValidationResult_WithErrors_ListsErrors()
    {
        var result = new ValidationResult
        {
            Issues = [new(ValidationSeverity.Error, "E001", "Something is wrong")],
        };
        var text = FormatHelpers.FormatValidationResult(result);

        text.Should().Contain("Errors");
        text.Should().Contain("Something is wrong");
    }

    [Fact]
    public void FormatValidationResult_WithWarnings_ListsWarnings()
    {
        var result = new ValidationResult
        {
            Issues = [new(ValidationSeverity.Warning, "W001", "Be careful")],
        };
        var text = FormatHelpers.FormatValidationResult(result);

        text.Should().Contain("Warnings");
        text.Should().Contain("Be careful");
    }

    [Fact]
    public void FormatValidationResult_BothErrorsAndWarnings_SeparatesSections()
    {
        var result = new ValidationResult
        {
            Issues =
            [
                new(ValidationSeverity.Error, "E001", "Bad thing"),
                new(ValidationSeverity.Warning, "W001", "Watch out"),
            ],
        };
        var text = FormatHelpers.FormatValidationResult(result);

        text.Should().Contain("Errors");
        text.Should().Contain("Warnings");
        var errorsIndex = text.IndexOf("Errors");
        var warningsIndex = text.IndexOf("Warnings");
        errorsIndex.Should().BeLessThan(warningsIndex);
    }

    // --- FormatCommandPreviewText ---

    [Fact]
    public void FormatCommandPreviewText_SingleCommand_ReturnsDisplayString()
    {
        var commands = new[] { MakeCommand("ffmpeg -i input.mp4 output.mp4") };
        FormatHelpers.FormatCommandPreviewText(commands).Should().Be("ffmpeg -i input.mp4 output.mp4");
    }

    [Fact]
    public void FormatCommandPreviewText_TwoPass_ShowsPassHeaders()
    {
        var commands = new[]
        {
            MakeCommand("ffmpeg -pass 1"),
            MakeCommand("ffmpeg -pass 2"),
        };
        var text = FormatHelpers.FormatCommandPreviewText(commands);

        text.Should().Contain("Pass 1");
        text.Should().Contain("Pass 2");
    }

    [Fact]
    public void FormatCommandPreviewText_SingleCommandFlag_JoinsWithAnd()
    {
        var commands = new[]
        {
            MakeCommand("cmd1"),
            MakeCommand("cmd2"),
        };
        var text = FormatHelpers.FormatCommandPreviewText(commands, singleCommand: true);
        text.Should().Be("cmd1 && cmd2");
    }

    [Fact]
    public void FormatCommandPreviewText_EmptyList_ReturnsEmpty()
    {
        FormatHelpers.FormatCommandPreviewText(Array.Empty<GeneratedCommand>()).Should().BeEmpty();
    }

    // --- FormatJobProgress ---

    [Fact]
    public void FormatJobProgress_AllFields_FormatsCorrectly()
    {
        var progress = new JobProgress(JobState.Running, 1, 2, 30.0, 15.0, 50.0, 2.0);
        var text = FormatHelpers.FormatJobProgress(progress);

        text.Should().Contain("Running");
        text.Should().Contain("Pass: 1 of 2");
        text.Should().MatchRegex(@"50[\.,]0%");
        text.Should().MatchRegex(@"2[\.,]00x");
    }

    // --- FormatJobResultSummary ---

    [Fact]
    public void FormatJobResultSummary_FormatsStateAndElapsed()
    {
        var result = new JobResult(
            new TestJobDefinitionBuilder().Build(),
            JobState.Completed,
            [MakeCommand("cmd")],
            [new ProcessExecutionResult(MakeCommand("cmd"), JobState.Completed, 0, 60.0, "")],
            60.0,
            "");

        var text = FormatHelpers.FormatJobResultSummary(result);
        text.Should().Contain("Completed");
        text.Should().Contain("1 of 1");
    }

    // --- FormatQueueSummary ---

    [Fact]
    public void FormatQueueSummary_EmptyQueue_ReturnsEmptyMessage()
    {
        FormatHelpers.FormatQueueSummary([]).Should().Contain("empty");
    }

    [Fact]
    public void FormatQueueSummary_MixedStates_ReturnsCorrectCounts()
    {
        var items = new[]
        {
            MakeQueueItem(JobState.Pending),
            MakeQueueItem(JobState.Completed),
            MakeQueueItem(JobState.Completed),
            MakeQueueItem(JobState.Failed),
        };
        var text = FormatHelpers.FormatQueueSummary(items);

        text.Should().Contain("Items: 4");
        text.Should().Contain("Pending: 1");
        text.Should().Contain("Completed: 2");
        text.Should().Contain("Failed: 1");
    }

    // --- Label functions ---

    [Theory]
    [InlineData(AudioCodec.Aac, "AAC")]
    [InlineData(AudioCodec.Mp3, "MP3")]
    [InlineData(AudioCodec.Vorbis, "Vorbis")]
    [InlineData(AudioCodec.Opus, "Opus")]
    public void AudioCodecLabel_AllValues_ReturnExpected(AudioCodec codec, string expected)
    {
        FormatHelpers.AudioCodecLabel(codec).Should().Be(expected);
    }

    [Theory]
    [InlineData(OutputContainer.Mp4, "MP4")]
    [InlineData(OutputContainer.Mkv, "MKV")]
    [InlineData(OutputContainer.Webm, "WEBM")]
    [InlineData(OutputContainer.Ogg, "OGG")]
    public void ContainerLabel_AllValues_ReturnExpected(OutputContainer container, string expected)
    {
        FormatHelpers.ContainerLabel(container).Should().Be(expected);
    }

    [Theory]
    [InlineData(ResolutionPreset.Original, "Original")]
    [InlineData(ResolutionPreset.P720, "720p")]
    [InlineData(ResolutionPreset.P1080, "1080p")]
    [InlineData(ResolutionPreset.P1440, "1440p")]
    [InlineData(ResolutionPreset.P4K, "4K")]
    public void ResolutionPresetLabel_AllValues_ReturnExpected(ResolutionPreset preset, string expected)
    {
        FormatHelpers.ResolutionPresetLabel(preset).Should().Be(expected);
    }

    [Theory]
    [InlineData(JobState.Pending, "Pending")]
    [InlineData(JobState.Running, "Running")]
    [InlineData(JobState.Completed, "Completed")]
    [InlineData(JobState.Failed, "Failed")]
    [InlineData(JobState.Canceled, "Canceled")]
    public void JobStateLabel_AllValues_ReturnExpected(JobState state, string expected)
    {
        FormatHelpers.JobStateLabel(state).Should().Be(expected);
    }

    // --- BuildJobDefinition ---

    [Fact]
    public void BuildJobDefinition_MapsAllFieldsFromSettings()
    {
        var settings = new TestOutputSettingsBuilder()
            .WithContainer(OutputContainer.Mkv)
            .WithVideoCodec(VideoCodec.H265)
            .WithStrategy(EncodingStrategy.TargetBitrate)
            .WithResolution(ResolutionPreset.P720)
            .Build();

        var job = FormatHelpers.BuildJobDefinition("/src.mp4", "/out.mkv", settings);

        job.SourcePath.Should().Be("/src.mp4");
        job.OutputPath.Should().Be("/out.mkv");
        job.Container.Should().Be(OutputContainer.Mkv);
        job.VideoCodec.Should().Be(VideoCodec.H265);
        job.EncodingStrategy.Should().Be(EncodingStrategy.TargetBitrate);
        job.ResolutionPreset.Should().Be(ResolutionPreset.P720);
    }

    // --- Helpers ---

    private static GeneratedCommand MakeCommand(string display) =>
        new("ffmpeg", [], display);

    private static QueueItem MakeQueueItem(JobState state)
    {
        var job = new TestJobDefinitionBuilder().Build();
        var media = new TestMediaInfoBuilder().Build();
        var queued = new QueuedJob(job, media, [], new ValidationResult());
        return new QueueItem(Guid.NewGuid().ToString(), queued, state);
    }
}
