using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class PresetViewModel : ObservableObject
{
    private static readonly PresetDefinition CurrentSettingsPreset =
        new("Current Settings", new OutputSettings());

    private readonly IPresetService _presetService;
    private readonly IDialogService _dialogService;
    private readonly Action<OutputSettings> _applyPresetCallback;
    private OutputSettings? _currentSettings;

    [ObservableProperty]
    private ObservableCollection<PresetDefinition> _presets = [CurrentSettingsPreset];

    [ObservableProperty]
    private PresetDefinition? _selectedPreset = CurrentSettingsPreset;

    [ObservableProperty]
    private bool _isPresetDirty;

    public PresetViewModel(
        IPresetService presetService,
        IDialogService dialogService,
        Action<OutputSettings> applyPresetCallback)
    {
        _presetService = presetService;
        _dialogService = dialogService;
        _applyPresetCallback = applyPresetCallback;
    }

    public async Task LoadPresetsAsync()
    {
        var loaded = await _presetService.LoadAllAsync();
        var all = new List<PresetDefinition> { CurrentSettingsPreset };
        all.AddRange(loaded);
        Presets = new ObservableCollection<PresetDefinition>(all);

        // Select "Current Settings" by default if nothing is selected
        SelectedPreset ??= CurrentSettingsPreset;
    }

    public void UpdateOutputSettings(OutputSettings settings)
    {
        _currentSettings = settings;
        SyncWithSettings(settings);
    }

    public void SyncWithSettings(OutputSettings settings)
    {
        // Check saved presets (skip the "Current Settings" sentinel)
        var match = Presets.FirstOrDefault(p => p != CurrentSettingsPreset && p.Settings == settings);
        if (match is not null)
        {
            IsPresetDirty = false;
            SelectedPreset = match;
            IsPresetDirty = false;
        }
        else if (SelectedPreset is null || SelectedPreset == CurrentSettingsPreset)
        {
            IsPresetDirty = false;
            SelectedPreset = CurrentSettingsPreset;
        }
        else
        {
            IsPresetDirty = true;
        }
    }

    public bool IsUserPresetSelected => SelectedPreset is not null && SelectedPreset != CurrentSettingsPreset;

    partial void OnSelectedPresetChanged(PresetDefinition? value)
    {
        OnPropertyChanged(nameof(IsUserPresetSelected));
        if (value is not null)
        {
            OnPresetSelected(value);
        }
    }

    private void OnPresetSelected(PresetDefinition preset)
    {
        IsPresetDirty = false;
        _applyPresetCallback.Invoke(preset.Settings);
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {

        // If no settings have been captured yet, use defaults
        _currentSettings ??= new OutputSettings();

        var name = await _dialogService.PromptTextAsync(
            "Save Preset", "Enter a name for this preset:",
            SelectedPreset?.Name);

        if (string.IsNullOrWhiteSpace(name))
            return;

        var preset = new PresetDefinition(name.Trim(), _currentSettings);
        await _presetService.SaveAsync(preset);
        await LoadPresetsAsync();

        SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name);
        IsPresetDirty = false;
    }

    [RelayCommand]
    private async Task RenamePresetAsync()
    {
        if (SelectedPreset is null)
            return;

        var newName = await _dialogService.PromptTextAsync(
            "Rename Preset", "Enter the new name:",
            SelectedPreset.Name);

        if (string.IsNullOrWhiteSpace(newName))
            return;

        var renamed = await _presetService.RenameAsync(SelectedPreset.Name, newName.Trim());
        await LoadPresetsAsync();

        SelectedPreset = Presets.FirstOrDefault(p => p.Name == renamed.Name);
    }

    [RelayCommand]
    private async Task DeletePresetAsync()
    {
        if (SelectedPreset is null)
            return;

        var confirmed = await _dialogService.ConfirmAsync(
            "Delete Preset",
            $"Delete preset \"{SelectedPreset.Name}\"?");

        if (!confirmed)
            return;

        await _presetService.DeleteAsync(SelectedPreset.Name);
        await LoadPresetsAsync();
        SelectedPreset = CurrentSettingsPreset;
    }
}
