using FFApp.Core.Models;
using FFApp.Core;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Helpers;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FFApp.Services;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public sealed class ComparisonViewModelTests
{
    [Fact]
    public async Task CancelDeleteComparison_RetainsClipsAndManifest()
    {
        var dialogs = Substitute.For<IDialogService>();
        dialogs.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(false);
        using var harness = new ComparisonTestHarness(dialogs);
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        await vm.RunAllCommand.ExecuteAsync(null);
        var count = vm.ReadyClipCount;
        await vm.DeleteComparisonCommand.ExecuteAsync(null);
        vm.IsDeleted.Should().BeFalse();
        vm.CanEdit.Should().BeTrue();
        vm.ReadyClipCount.Should().Be(count);
        (await harness.Service.LoadAsync(harness.Context)).Results.Should().HaveCount(count);
    }

    [Fact]
    public async Task DeleteTestClips_KeepsEditedConfiguration_AndResetsResults()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        await vm.RunAllCommand.ExecuteAsync(null);
        vm.ReadyClipCount.Should().BeGreaterThan(0);
        vm.CacheUsageLabel.Should().NotBe("Test clips: 0 B");
        vm.Samples[0].Name = "Keep this sample";
        vm.Variants[0].Name = "Keep this variant";
        await vm.ClearTestClipsCommand.ExecuteAsync(null);
        vm.ReadyClipCount.Should().Be(0);
        vm.MissingClipCount.Should().Be(vm.Samples.Count * vm.Variants.Count);
        vm.CacheUsageLabel.Should().Be("Test clips: 0 B");
        await vm.CloseAsync();
        var saved = await harness.Service.LoadAsync(harness.Context);
        saved.Samples[0].Name.Should().Be("Keep this sample");
        saved.Variants[0].Name.Should().Be("Keep this variant");
    }

    [Fact]
    public async Task DeleteComparison_CloseDoesNotRecreateManifest()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        await harness.ViewModel.RunAllCommand.ExecuteAsync(null);
        await harness.ViewModel.DeleteComparisonCommand.ExecuteAsync(null);
        harness.ViewModel.IsDeleted.Should().BeTrue();
        (await harness.ViewModel.CloseAsync()).Should().BeTrue();
        Directory.GetFiles(Path.Combine(harness.DirectoryPath, "comparison"), "*.json").Should().BeEmpty();
        File.Exists(harness.Context.MediaInfo.SourcePath).Should().BeTrue();
    }

    [Fact]
    public async Task MaximizePreview_TogglesPanels_AndResetsWhenChangingSteps()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        await vm.RunAllCommand.ExecuteAsync(null);
        vm.ShowResultsCommand.Execute(null);
        vm.ShowSamplePanel.Should().BeTrue();
        vm.ShowResultTable.Should().BeTrue();

        using (var changes = vm.Monitor())
        {
            vm.TogglePreviewSizeCommand.Execute(null);
            changes.Should().RaisePropertyChangeFor(v => v.ShowSamplePanel);
            changes.Should().RaisePropertyChangeFor(v => v.ShowResultTable);
            changes.Should().RaisePropertyChangeFor(v => v.PreviewSizeActionLabel);
        }
        vm.IsPreviewMaximized.Should().BeTrue();
        vm.ShowSamplePanel.Should().BeFalse();
        vm.ShowResultTable.Should().BeFalse();
        vm.PreviewSizeActionLabel.Should().Be("Restore panels");

        vm.TogglePreviewSizeCommand.Execute(null);
        vm.ShowSamplePanel.Should().BeTrue();
        vm.ShowResultTable.Should().BeTrue();
        vm.PreviewSizeActionLabel.Should().Be("Maximize videos");

        vm.TogglePreviewSizeCommand.Execute(null);
        vm.ShowVariantsCommand.Execute(null);
        vm.IsPreviewMaximized.Should().BeFalse();
        vm.ShowSamplePanel.Should().BeTrue();
        vm.ShowResultTable.Should().BeFalse();
        vm.ShowResultsCommand.Execute(null);
        vm.ShowResultTable.Should().BeTrue();
    }

    [Fact]
    public async Task GrainVariants_KeepCrfAndPreset_AndEnableSourceDenoising()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        vm.AddGrainVariantsCommand.Execute(null);
        vm.Variants.Should().HaveCount(3);
        var quality = vm.Variants.Select(v => (Av1QualityOptions)v.Settings.TargetQuality).ToArray();
        quality.Select(q => q.FilmGrain).Should().Equal(0, 8, 10);
        quality.Should().OnlyContain(q => q.Crf == 24 && q.Preset == 8 && (q.FilmGrain == 0 || q.FilmGrainDenoise == 1));
        vm.AddGrainVariantsCommand.Execute(null);
        vm.Variants.Should().HaveCount(3, "the shortcut must not duplicate existing variants");
        vm.Variants.Skip(1).Should().OnlyContain(v => v.Settings.AudioOptions == harness.Context.Settings.AudioOptions);
        harness.Applied.Should().BeNull();
    }

    [Fact]
    public async Task EditingDuplicate_DoesNotChangeBaselineOrMainWindow_AndApplyIsExplicit()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        var baseline = vm.Variants[0].Settings;
        vm.DuplicateVariantCommand.Execute(null);
        vm.Editor.Strategy.Quality.Av1.Crf = 28;
        vm.Variants[0].Settings.Should().Be(baseline);
        vm.SelectedVariant?.Settings.TargetQuality.Should().BeOfType<Av1QualityOptions>().Which.Crf.Should().Be(28);
        harness.Applied.Should().BeNull();
        vm.ApplyVariantCommand.Execute(null);
        harness.Applied?.TargetQuality.Should().BeOfType<Av1QualityOptions>().Which.Crf.Should().Be(28);
        harness.AcceptApply = false;
        vm.ApplyVariantCommand.Execute(null);
        vm.Status.Should().Contain("Select this comparison's source");
    }

    [Fact]
    public async Task Run_ProducesMatrix_PersistsResults_AndReusesUnchangedClips()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        vm.SampleCount = 2;
        vm.DistributeCommand.Execute(null);
        vm.AddGrainVariantsCommand.Execute(null);
        await vm.RunAllCommand.ExecuteAsync(null);
        vm.Rows.Should().HaveCount(2);
        vm.Rows.Should().OnlyContain(r => r.Cells.Count == 3 && r.Cells.All(c => c.Clip != null));
        vm.Progress.Should().Be(100);
        var calls = harness.Execution.ReceivedCalls().Count();
        await vm.RunAllCommand.ExecuteAsync(null);
        harness.Execution.ReceivedCalls().Should().HaveCount(calls);
        vm.Rows.Should().OnlyContain(r => r.Cells.All(c => c.Status == "Cached"));
        var restored = await harness.Service.LoadAsync(harness.Context);
        restored.Results.Should().HaveCount(6);
        restored.Samples.Should().HaveCount(2);
        restored.Variants.Should().HaveCount(3);
    }

    [Fact]
    public async Task EditingSampleOrVariant_InvalidatesOnlyAffectedCells()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        vm.SampleCount = 2;
        vm.DistributeCommand.Execute(null);
        vm.DuplicateVariantCommand.Execute(null);
        await vm.RunAllCommand.ExecuteAsync(null);
        vm.Samples[0].StartSeconds += 1;
        vm.Rows[0].Cells.Should().OnlyContain(c => c.Clip == null);
        vm.Rows[1].Cells.Should().OnlyContain(c => c.Clip != null);
        vm.Editor.Strategy.Quality.Av1.Crf += 1;
        vm.Rows[1].Cells[0].Clip.Should().NotBeNull();
        vm.Rows[1].Cells[1].Clip.Should().BeNull();
    }

    [Fact]
    public async Task InvalidSample_IsReportedWithoutEncodingOrOverwritingSavedSession()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        (await vm.TrySaveAsync()).Should().BeTrue();
        vm.Samples[0].StartSeconds = 119;
        await vm.RunAllCommand.ExecuteAsync(null);
        vm.Status.Should().Contain("inside the saved trim range");
        harness.Execution.ReceivedCalls().Should().BeEmpty();
        (await vm.TrySaveAsync()).Should().BeFalse();
        (await harness.Service.LoadAsync(harness.Context)).Samples[0].StartSeconds.Should().NotBe(119);
    }

    [Fact]
    public void Differences_IncludeToneMapParameters_AndOnlyChangedFields()
    {
        var baseline = new OutputSettings();
        var changed = baseline with { ToneMapSettings = baseline.ToneMapSettings with { ContrastRecovery = 0.7, Saturation = 0.9 } };
        var text = ComparisonSettingsDescription.Differences(baseline, changed);
        text.Should().Contain("Contrast recovery: 0.3 → 0.7").And.Contain("Saturation: 1 → 0.9");
        text.Should().NotContain("CRF").And.NotContain("tone_map_settings");
    }

    [Fact]
    public async Task ReturningToEarlierSettings_ReusesPreviouslyCompletedVersion()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        vm.SampleCount = 1;
        vm.DistributeCommand.Execute(null);
        await vm.RunAllCommand.ExecuteAsync(null);
        vm.Editor.Strategy.Quality.Av1.Crf = 28;
        await vm.RunAllCommand.ExecuteAsync(null);
        var calls = harness.Execution.ReceivedCalls().Count();
        vm.Editor.Strategy.Quality.Av1.Crf = 24;
        await vm.RunAllCommand.ExecuteAsync(null);
        harness.Execution.ReceivedCalls().Should().HaveCount(calls);
        vm.Rows[0].Cells[0].Status.Should().Be("Cached");
    }

    [Fact]
    public async Task SourcePreview_IsAvailableBeforeEncoding_AndUsesSingleView()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        harness.ViewModel.PreviewSelection();
        harness.ViewModel.IsSamplesStep.Should().BeTrue();
        harness.ViewModel.HasResults.Should().BeFalse();
        harness.Playback.IsSourcePreview.Should().BeTrue();
        harness.Playback.LeftTrack?.Path.Should().Be(harness.Context.MediaInfo.SourcePath);
        harness.Playback.LeftTrack?.OffsetSeconds.Should().Be(harness.ViewModel.Samples[0].StartSeconds);
        harness.Playback.RightTrack.Should().BeNull();
        harness.Playback.DurationSeconds.Should().Be(harness.ViewModel.Samples[0].DurationSeconds);
        harness.Playback.Layout.Should().Be("A only");
        harness.Execution.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task SelectingAndEditingSamples_UpdatesOriginalPreviewWithoutEncoding()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        vm.PreviewSelection();
        vm.SelectedSample = vm.Samples[2];
        harness.Playback.LeftTrack?.OffsetSeconds.Should().Be(vm.Samples[2].StartSeconds);
        vm.Samples[2].StartSeconds += 1;
        vm.Samples[2].DurationSeconds = 8;
        harness.Playback.LeftTrack?.OffsetSeconds.Should().Be(vm.Samples[2].StartSeconds);
        harness.Playback.DurationSeconds.Should().Be(8);
        harness.Playback.RightTrack.Should().BeNull();
        harness.Execution.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task PrimaryAction_GuidesFromPreviewThroughVariantsToResults_WithAccurateCounts()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        vm.PreviewSelection();
        vm.PrimaryActionLabel.Should().Be("Next: choose variants →");
        vm.ShowResultsCommand.CanExecute(null).Should().BeFalse();
        vm.ShowResultTable.Should().BeFalse();
        await vm.PrimaryActionCommand.ExecuteAsync(null);
        vm.IsVariantsStep.Should().BeTrue();
        harness.Execution.ReceivedCalls().Should().BeEmpty();
        vm.SampleCount = 2;
        vm.DistributeCommand.Execute(null);
        vm.AddGrainVariantsCommand.Execute(null);
        vm.PrimaryActionLabel.Should().Be("Create test clips (6)");
        vm.ClipPlan.Should().Contain("2 samples × 3 variants").And.Contain("6 to create");
        await vm.PrimaryActionCommand.ExecuteAsync(null);
        vm.IsResultsStep.Should().BeTrue();
        vm.ShowResultTable.Should().BeTrue();
        vm.MissingClipCount.Should().Be(0);
        harness.Playback.IsSourcePreview.Should().BeFalse();
        harness.Playback.RightTrack.Should().NotBeNull();
        vm.Rows[0].Cells[2].SelectCommand.Execute(null);
        vm.PrimaryActionLabel.Should().Be("Apply Grain 10 to main window");
        await vm.PrimaryActionCommand.ExecuteAsync(null);
        harness.Applied.Should().Be(vm.Variants[2].Settings);
    }

    [Fact]
    public async Task ReturningToSamples_AfterEncoding_ShowsOriginalAndHidesResults()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        vm.PreviewSelection();
        vm.SampleCount = 1;
        vm.DistributeCommand.Execute(null);
        await vm.RunAllCommand.ExecuteAsync(null);
        vm.ShowResultTable.Should().BeTrue();
        vm.ShowSamplesCommand.Execute(null);
        vm.ShowResultTable.Should().BeFalse();
        harness.Playback.IsSourcePreview.Should().BeTrue();
        harness.Playback.RightTrack.Should().BeNull();
        vm.SelectedSample = vm.Samples[0];
        vm.Samples[0].StartSeconds += 1;
        vm.HasResults.Should().BeFalse();
        vm.MissingClipCount.Should().Be(1);
    }

    [Fact]
    public async Task ReopenedComparison_StartsWithOriginal_AndOffersExistingResults()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        await harness.ViewModel.RunAllCommand.ExecuteAsync(null);
        var messenger = new WeakReferenceMessenger();
        var editor = new OutputSettingsViewModel(new ValidationService(), messenger,
            Substitute.For<IFfmpegCapabilityService>(), new ExecutablePathsConfig());
        using var playback = new ComparisonPlaybackViewModel(null);
        using var reopened = new ComparisonViewModel(harness.Service, editor, messenger, playback);
        await reopened.LoadAsync(harness.Context, _ => true);
        reopened.PreviewSelection();
        reopened.IsSamplesStep.Should().BeTrue();
        playback.IsSourcePreview.Should().BeTrue();
        reopened.HasResults.Should().BeTrue();
        reopened.ShowResultTable.Should().BeFalse();
        reopened.ShowResultsCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task ClosingDuringEncoding_CancelsActiveJob_AndPreservesCompletedWork()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Execution.ExecuteAsync(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>(), Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(), Arg.Any<ExecutionPauseState?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var job = call.Arg<JobDefinition>();
                File.WriteAllText(job.OutputPath, "partial");
                started.SetResult();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return new JobResult(job, JobState.Completed, [], [], 1, "");
            });
        var run = harness.ViewModel.RunAllCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.ViewModel.CanEdit.Should().BeFalse();
        harness.ViewModel.RunAllCommand.CanExecute(null).Should().BeFalse();
        (await harness.ViewModel.CloseAsync()).Should().BeTrue();
        await run;
        harness.ViewModel.IsRunning.Should().BeFalse();
        Directory.EnumerateFiles(harness.DirectoryPath, "*.partial.*", SearchOption.AllDirectories).Should().BeEmpty();
        (await harness.Service.LoadAsync(harness.Context)).Samples.Should().HaveCount(8);
    }

    [Fact]
    public async Task CellSelection_ChoosesMatchingSampleAndVariant()
    {
        using var harness = new ComparisonTestHarness();
        await harness.LoadAsync();
        var vm = harness.ViewModel;
        vm.AddGrainVariantsCommand.Execute(null);
        vm.Rows[1].Cells[2].SelectCommand.Execute(null);
        vm.SelectedSample.Should().BeSameAs(vm.Samples[1]);
        vm.RightChoice?.Id.Should().Be(vm.Variants[2].Id);
        vm.SelectedTab.Should().Be(2);
    }

    [Theory]
    [InlineData(200, 0, 12, 200000)]
    [InlineData(2, 5, 12, 7000)]
    [InlineData(2, -10, 12, 2000)]
    [InlineData(2, 99, 12, 13960)]
    public void PlaybackTime_MapsSourceAndClipToSameSamplePosition(double offset, double position, double duration, long expected)
    {
        ComparisonPlaybackViewModel.MediaTime(new ComparisonTrack("/video", offset), position, duration).Should().Be(expected);
    }
}
