using FFApp.Core.Models;
using FFApp.Core.Services;
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

        _sut = new PresetViewModel(_presetService, _dialogService, s => _appliedSettings = s);
    }

    [Fact]
    public async Task LoadPresetsAsync_AddsCurrentSettingsFirst()
    {
        await _sut.LoadPresetsAsync();

        _sut.Presets.Should().HaveCountGreaterThanOrEqualTo(1);
        _sut.Presets[0].Name.Should().Be("Current Settings");
    }

    [Fact]
    public async Task LoadPresetsAsync_IncludesLoadedPresets()
    {
        var userPreset = new PresetDefinition("My Preset", new OutputSettings());
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([userPreset]);

        await _sut.LoadPresetsAsync();

        _sut.Presets.Should().HaveCount(2);
        _sut.Presets[1].Name.Should().Be("My Preset");
    }

    [Fact]
    public async Task SavePresetAsync_UserCancels_ServiceNotCalled()
    {
        _dialogService.PromptTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns((string?)null);

        await _sut.SavePresetCommand.ExecuteAsync(null);

        await _presetService.DidNotReceive().SaveAsync(Arg.Any<PresetDefinition>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SavePresetAsync_UserEntersName_ServiceCalled()
    {
        _dialogService.PromptTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns("New Preset");

        await _sut.SavePresetCommand.ExecuteAsync(null);

        await _presetService.Received().SaveAsync(
            Arg.Is<PresetDefinition>(p => p.Name == "New Preset"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePresetAsync_UserConfirms_ServiceCalled()
    {
        var userPreset = new PresetDefinition("To Delete", new OutputSettings());
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([userPreset]);
        await _sut.LoadPresetsAsync();
        _sut.SelectedPreset = _sut.Presets[1];

        _dialogService.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(true);

        await _sut.DeletePresetCommand.ExecuteAsync(null);

        await _presetService.Received().DeleteAsync("To Delete", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeletePresetAsync_UserDeclines_ServiceNotCalled()
    {
        var userPreset = new PresetDefinition("Keep Me", new OutputSettings());
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([userPreset]);
        await _sut.LoadPresetsAsync();
        _sut.SelectedPreset = _sut.Presets[1];

        _dialogService.ConfirmAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(false);

        await _sut.DeletePresetCommand.ExecuteAsync(null);

        await _presetService.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SelectedPresetChange_InvokesCallback()
    {
        var preset = new PresetDefinition("Test", new OutputSettings(Container: OutputContainer.Mkv));

        _sut.SelectedPreset = preset;

        _appliedSettings.Should().NotBeNull();
        _appliedSettings!.Container.Should().Be(OutputContainer.Mkv);
    }

    [Fact]
    public async Task IsUserPresetSelected_CurrentSettings_ReturnsFalse()
    {
        await _sut.LoadPresetsAsync();
        _sut.SelectedPreset = _sut.Presets[0]; // "Current Settings"

        _sut.IsUserPresetSelected.Should().BeFalse();
    }

    [Fact]
    public async Task IsUserPresetSelected_UserPreset_ReturnsTrue()
    {
        var userPreset = new PresetDefinition("User One", new OutputSettings());
        _presetService.LoadAllAsync(Arg.Any<CancellationToken>())
            .Returns([userPreset]);
        await _sut.LoadPresetsAsync();

        _sut.SelectedPreset = _sut.Presets[1];

        _sut.IsUserPresetSelected.Should().BeTrue();
    }
}
