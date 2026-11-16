using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Core.Validation;
using FFApp.Infrastructure.Serialization;
using FFApp.Services;
using FFApp.Tests.Builders;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public sealed class WorkspacePresetTests
{
    private readonly List<PresetDefinition> _storedPresets = [];
    private readonly WorkspaceViewModel _workspace;

    public WorkspacePresetTests()
    {
        var messenger = new WeakReferenceMessenger();
        var dialogs = Substitute.For<IDialogService>();
        dialogs.PromptTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns("Audio movie");
        var presetService = Substitute.For<IPresetService>();
        presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyList<PresetDefinition>>([.. _storedPresets]));
        presetService.SaveAsync(Arg.Any<PresetDefinition>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                // Store the preset the way PresetService does: through the JSON serializer.
                var json = SettingsSerializer.SerializePreset(call.Arg<PresetDefinition>());
                using var doc = JsonDocument.Parse(json.ToJsonString());
                _storedPresets.Add(SettingsSerializer.DeserializePreset(doc.RootElement));
                return Task.CompletedTask;
            });

        var sources = new SourceListPanelViewModel(Substitute.For<IMediaProbeService>(), dialogs, messenger);
        var paths = new ExecutablePathsConfig();
        var builder = new DefaultCommandBuilder(paths);
        var validator = new ValidationService();
        var settings = new OutputSettingsViewModel(validator, messenger, Substitute.For<IFfmpegCapabilityService>(), paths);
        var preview = new VideoPreviewViewModel(null);
        var queue = new JobListPanelViewModel(Substitute.For<IQueueService>(), builder, validator, dialogs, sources, preview, TimeProvider.System);
        _workspace = new WorkspaceViewModel(sources, new OutputPathViewModel(dialogs), settings,
            new PresetViewModel(presetService, dialogs), new CommandPreviewViewModel(builder), queue, preview, messenger);
        messenger.Send(new SourceLoadedMessage(new TestMediaInfoBuilder().WithHdr10().Build()));
        settings.ApplySettings(new OutputSettings(Container: OutputContainer.Mkv, VideoCodec: VideoCodec.Av1));
    }

    [Fact]
    public async Task SaveAs_InCopyMode_KeepsSavedSettingsAndRestoresThemOnReselect()
    {
        var format = _workspace.OutputSettings.Format;
        format.SelectedVideoMode = VideoMode.Copy;
        var saved = _workspace.OutputSettings.BuildSettings();

        await _workspace.Preset.SavePresetCommand.ExecuteAsync(null);

        format.SelectedVideoMode.Should().Be(VideoMode.Copy);
        _workspace.OutputSettings.BuildSettings().Should().Be(saved);
        _workspace.Preset.IsPresetDirty.Should().BeFalse();

        var preset = _workspace.Preset.SelectedPreset ?? throw new InvalidOperationException("Preset not selected.");
        _workspace.Preset.SelectedPreset = null;
        format.SelectedVideoMode = VideoMode.Reencode;
        _workspace.Preset.SelectedPreset = preset;

        format.SelectedVideoMode.Should().Be(VideoMode.Copy);
        _workspace.OutputSettings.BuildSettings().Should().Be(saved);
    }
}
