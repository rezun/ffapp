using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Infrastructure;
using FFApp.ViewModels;
using NSubstitute;

namespace FFApp.Tests.Builders;

public sealed class ComparisonTestHarness : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ffapp-comparison-vm-" + Guid.NewGuid().ToString("N"));
    public IJobExecutionService Execution { get; } = Substitute.For<IJobExecutionService>();
    public JobContext Context { get; }
    public ComparisonService Service { get; }
    public ComparisonViewModel ViewModel { get; }
    public ComparisonPlaybackViewModel Playback { get; } = new(null);
    public OutputSettings? Applied { get; private set; }
    public bool AcceptApply { get; set; } = true;

    public ComparisonTestHarness()
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, "Film.mkv");
        File.WriteAllText(path, "source");
        var settings = new OutputSettings(VideoCodec: VideoCodec.Av1,
            VideoEncoderOptions: new Av1EncoderOptions(Preset: 8),
            TargetQuality: new Av1QualityOptions(Crf: 24, Preset: 8));
        Context = new JobContext(new TestMediaInfoBuilder().WithSourcePath(path).WithDuration(120).Build(), settings);
        var validation = new ValidationService();
        var config = new ExecutablePathsConfig();
        Service = new ComparisonService(new DefaultCommandBuilder(config), Execution,
            validation, new JsonSerializerOptions(), Path.Combine(DirectoryPath, "comparison"));
        var messenger = new WeakReferenceMessenger();
        var editor = new OutputSettingsViewModel(validation, messenger, Substitute.For<IFfmpegCapabilityService>(), config) { IsVideoModeEditable = false };
        ViewModel = new ComparisonViewModel(Service, editor, messenger, Playback);
        Execution.ExecuteAsync(Arg.Any<JobDefinition>(), Arg.Any<SourceMediaInfo>(), Arg.Any<Action<JobProgress>?>(),
            Arg.Any<Action<string>?>(), Arg.Any<ExecutionPauseState?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var job = call.Arg<JobDefinition>();
                var grain = job.TargetQuality is Av1QualityOptions q ? q.FilmGrain : 0;
                File.WriteAllBytes(job.OutputPath, new byte[1000 - grain * 20]);
                return Task.FromResult(new JobResult(job, JobState.Completed, [], [], 3, ""));
            });
    }

    public Task LoadAsync() => ViewModel.LoadAsync(Context, s =>
    {
        if (!AcceptApply) return false;
        Applied = s;
        return true;
    });

    public void Dispose()
    {
        ViewModel.Dispose();
        Playback.Dispose();
        Directory.Delete(DirectoryPath, recursive: true);
    }
}
