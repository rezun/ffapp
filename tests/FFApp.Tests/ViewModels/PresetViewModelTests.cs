using System.Text.Json;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure.Serialization;
using FFApp.Services;
using FFApp.ViewModels;
using FluentAssertions;
using NSubstitute;

namespace FFApp.Tests.ViewModels;

public class PresetViewModelTests
{
    private readonly IPresetService _presetService = Substitute.For<IPresetService>();
    private readonly IDialogService _dialogService = Substitute.For<IDialogService>();
    private OutputSettings? _appliedSettings;
    private readonly PresetViewModel _sut;

    public PresetViewModelTests()
    {
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        _sut = new PresetViewModel(_presetService, _dialogService);
        _sut.PresetApplied += s => _appliedSettings = s;
    }

    [Fact]
    public async Task DeletePresetAsync_UserConfirms_ServiceCalledAndSelectionCleared()
    {
        var userPreset = new PresetDefinition("To Delete", new OutputSettings());
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([userPreset]);
        await _sut.LoadPresetsAsync();
        _sut.SelectedPreset = _sut.Presets[0];

        // Second load after delete returns empty library.
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _dialogService.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        await _sut.DeletePresetCommand.ExecuteAsync(null);

        await _presetService.Received().DeleteAsync("To Delete", Arg.Any<CancellationToken>());
        _sut.SelectedPreset.Should().BeNull();
    }

    [Fact]
    public async Task DeletePresetAsync_UserDeclines_ServiceNotCalled()
    {
        var userPreset = new PresetDefinition("Keep Me", new OutputSettings());
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([userPreset]);
        await _sut.LoadPresetsAsync();
        _sut.SelectedPreset = _sut.Presets[0];

        _dialogService.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);

        await _sut.DeletePresetCommand.ExecuteAsync(null);

        await _presetService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncWithSettings_MatchesPreset_SelectsItAndClearsDirty()
    {
        var settings = new OutputSettings(Container: OutputContainer.Webm, VideoCodec: VideoCodec.Vp9);
        var preset = new PresetDefinition("Web", settings);
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([preset]);
        await _sut.LoadPresetsAsync();

        _sut.UpdateOutputSettings(settings);

        _sut.SelectedPreset.Should().Be(preset);
        _sut.IsPresetDirty.Should().BeFalse();
    }

    [Fact]
    public async Task SyncWithSettings_PresetSelectedAndValuesDrift_MarksDirty()
    {
        var original = new OutputSettings(Container: OutputContainer.Mp4);
        var preset = new PresetDefinition("Base", original);
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([preset]);
        await _sut.LoadPresetsAsync();
        _sut.SelectedPreset = _sut.Presets[0];

        var drifted = new OutputSettings(Container: OutputContainer.Mkv);
        _sut.UpdateOutputSettings(drifted);

        _sut.IsPresetDirty.Should().BeTrue();
        _sut.SelectedPreset.Should().Be(preset);
    }

    [Fact]
    public async Task SyncWithSettings_NoSelection_NoMatch_StaysCleanAndNull()
    {
        var preset = new PresetDefinition("Base", new OutputSettings(Container: OutputContainer.Mp4));
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([preset]);
        await _sut.LoadPresetsAsync();

        _sut.UpdateOutputSettings(new OutputSettings(Container: OutputContainer.Mkv));

        _sut.SelectedPreset.Should().BeNull();
        _sut.IsPresetDirty.Should().BeFalse();
    }

    [Fact]
    public async Task RestoreSelection_NameMatches_SelectsWithoutInvokingCallback()
    {
        var preset = new PresetDefinition("Saved", new OutputSettings(Container: OutputContainer.Mkv));
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([preset]);
        await _sut.LoadPresetsAsync();

        _sut.RestoreSelection("Saved");

        _sut.SelectedPreset.Should().Be(preset);
        _appliedSettings.Should().BeNull();
    }

    [Fact]
    public async Task RestoreSelection_NameMissing_LeavesSelectionNull()
    {
        var preset = new PresetDefinition("Only", new OutputSettings());
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([preset]);
        await _sut.LoadPresetsAsync();

        _sut.RestoreSelection("Ghost");

        _sut.SelectedPreset.Should().BeNull();
    }

    [Fact]
    public async Task RestoreSelection_WithDriftedSettings_MarksDirty()
    {
        var presetSettings = new OutputSettings(Container: OutputContainer.Mp4);
        var preset = new PresetDefinition("Base", presetSettings);
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([preset]);
        await _sut.LoadPresetsAsync();

        // Simulate the workspace having hydrated drifted settings before restoring the selection.
        _sut.UpdateOutputSettings(new OutputSettings(Container: OutputContainer.Mkv));
        _sut.RestoreSelection("Base");

        _sut.SelectedPreset.Should().Be(preset);
        _sut.IsPresetDirty.Should().BeTrue();
    }

    [Fact]
    public async Task RevertPresetCommand_Dirty_AppliesOriginalAndClearsDirty()
    {
        var original = new OutputSettings(Container: OutputContainer.Mp4);
        var preset = new PresetDefinition("Base", original);
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([preset]);
        await _sut.LoadPresetsAsync();
        _sut.SelectedPreset = _sut.Presets[0];

        // Drift settings to mark dirty.
        _sut.UpdateOutputSettings(new OutputSettings(Container: OutputContainer.Mkv));
        _sut.IsPresetDirty.Should().BeTrue();
        _sut.RevertPresetCommand.CanExecute(null).Should().BeTrue();

        _appliedSettings = null;
        _sut.RevertPresetCommand.Execute(null);

        _appliedSettings.Should().Be(original);

        // Simulate the workspace echoing the reverted settings back through the message bus.
        _sut.UpdateOutputSettings(original);
        _sut.IsPresetDirty.Should().BeFalse();
    }

    [Fact]
    public async Task RevertPresetCommand_LegacyPresetWithoutEncoderOptions_ClearsDirtyAfterOutputSettingsRebuild()
    {
        var json = """
        {
            "video_codec": "h264",
            "container": "mp4",
            "target_quality": { "codec": "h264", "crf": 18, "preset": "slow", "profile": "high" }
        }
        """;
        using var doc = JsonDocument.Parse(json);
        var legacySettings = SettingsSerializer.DeserializeOutputSettings(doc.RootElement);
        var preset = new PresetDefinition("Legacy", legacySettings);
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([preset]);

        var messenger = new WeakReferenceMessenger();
        var validationService = Substitute.For<IValidationService>();
        var capabilityService = Substitute.For<IFfmpegCapabilityService>();
        var outputSettings = new OutputSettingsViewModel(
            validationService, messenger, capabilityService, new ExecutablePathsConfig());
        _sut.PresetApplied += outputSettings.ApplySettings;
        messenger.Register<OutputSettingsChangedMessage>(
            this,
            (_, message) => _sut.UpdateOutputSettings(message.Value));

        await _sut.LoadPresetsAsync();
        _sut.SelectedPreset = _sut.Presets[0];
        outputSettings.ApplySettings(legacySettings with { Container = OutputContainer.Mkv });

        _sut.IsPresetDirty.Should().BeTrue();

        _sut.RevertPresetCommand.Execute(null);

        _sut.IsPresetDirty.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RevertPresetCommand_RequiresSelectionAndDirty(bool selectPreset, bool dirty)
    {
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([new PresetDefinition("Base", new OutputSettings())]);
        await _sut.LoadPresetsAsync();
        if (selectPreset)
            _sut.SelectedPreset = _sut.Presets[0];

        _sut.IsPresetDirty = dirty;

        _sut.RevertPresetCommand.CanExecute(null).Should().BeFalse();
    }
}
