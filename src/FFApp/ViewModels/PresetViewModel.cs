using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class PresetViewModel : ObservableObject
{
    private readonly IPresetService _presetService;
    private readonly IDialogService _dialogService;
    private OutputSettings? _currentSettings;

    [ObservableProperty]
    private ObservableCollection<PresetDefinition> _presets = [];

    [ObservableProperty]
    private PresetDefinition? _selectedPreset;

    [ObservableProperty]
    private bool _isPresetDirty;

    /// <summary>
    /// Raised when the user selects a preset so the workspace can apply the settings.
    /// </summary>
    public event Action<OutputSettings>? PresetApplied;

    public PresetViewModel(
        IPresetService presetService,
        IDialogService dialogService)
    {
        _presetService = presetService;
        _dialogService = dialogService;
    }

    public async Task LoadPresetsAsync()
    {
        var loaded = await _presetService.LoadAllAsync();
        Presets = new ObservableCollection<PresetDefinition>(loaded);
    }

    /// <summary>
    /// Restores the selected preset by name without applying its settings to the
    /// workspace. Used at startup when the workspace settings have already been
    /// hydrated from <c>LastUsedSettings</c> and we only need to re-establish the
    /// association (and any resulting dirty state).
    /// </summary>
    public void RestoreSelection(string? presetName)
    {
        if (string.IsNullOrEmpty(presetName))
            return;

        var match = Presets.FirstOrDefault(p => p.Name == presetName);
        if (match is null)
            return;

        // Assign via backing field to avoid invoking PresetApplied — the workspace
        // already has the user's last-used settings and must not be overwritten.
#pragma warning disable MVVMTK0034
        _selectedPreset = match;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(nameof(IsUserPresetSelected));

        // Re-evaluate dirty state against the already-hydrated workspace settings.
        // SyncWithSettings may have run earlier with SelectedPreset null (taking the
        // no-op branch); now that a preset is associated, any divergence between the
        // preset's saved values and the current workspace state must mark it dirty.
        if (_currentSettings is not null)
            SyncWithSettings(_currentSettings);
    }

    public void UpdateOutputSettings(OutputSettings settings)
    {
        _currentSettings = settings;
        SyncWithSettings(settings);
    }

    public void SyncWithSettings(OutputSettings settings)
    {
        // Prefer value-equality match: if current settings exactly match a saved
        // preset, align to it and clear dirty — this mirrors in-session behavior
        // where editing toward a preset's values re-selects it.
        var match = Presets.FirstOrDefault(p => p.Settings == settings);
        if (match is not null)
        {
            SelectedPreset = match;
            IsPresetDirty = false;
        }
        else if (SelectedPreset is null)
        {
            // No preset selected and no match — stay in the "(No preset)" state.
            IsPresetDirty = false;
        }
        else
        {
            // A preset is selected but current settings drifted from it.
            IsPresetDirty = true;
        }
        RevertPresetCommand.NotifyCanExecuteChanged();
    }

    public bool IsUserPresetSelected => SelectedPreset is not null;

    partial void OnSelectedPresetChanged(PresetDefinition? value)
    {
        OnPropertyChanged(nameof(IsUserPresetSelected));
        RevertPresetCommand.NotifyCanExecuteChanged();
        if (value is not null)
        {
            IsPresetDirty = false;
            PresetApplied?.Invoke(value.Settings);
        }
    }

    partial void OnIsPresetDirtyChanged(bool value)
    {
        RevertPresetCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task SavePresetAsync(CancellationToken ct)
    {
        // If no settings have been captured yet, use defaults
        _currentSettings ??= new OutputSettings();

        var name = await _dialogService.PromptTextAsync(
            "Save Preset", "Enter a name for this preset:",
            SelectedPreset?.Name);

        if (string.IsNullOrWhiteSpace(name))
            return;

        var preset = new PresetDefinition(name.Trim(), _currentSettings);
        await _presetService.SaveAsync(preset, ct);
        await LoadPresetsAsync();

        SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name);
        IsPresetDirty = false;
    }

    [RelayCommand]
    private async Task RenamePresetAsync(CancellationToken ct)
    {
        if (SelectedPreset is null)
            return;

        var newName = await _dialogService.PromptTextAsync(
            "Rename Preset", "Enter the new name:",
            SelectedPreset.Name);

        if (string.IsNullOrWhiteSpace(newName))
            return;

        var renamed = await _presetService.RenameAsync(SelectedPreset.Name, newName.Trim(), ct);
        await LoadPresetsAsync();

        SelectedPreset = Presets.FirstOrDefault(p => p.Name == renamed.Name);
    }

    [RelayCommand]
    private async Task DeletePresetAsync(CancellationToken ct)
    {
        if (SelectedPreset is null)
            return;

        var confirmed = await _dialogService.ConfirmAsync(
            "Delete Preset",
            $"Delete preset \"{SelectedPreset.Name}\"?");

        if (!confirmed)
            return;

        await _presetService.DeleteAsync(SelectedPreset.Name, ct);
        await LoadPresetsAsync();
        // Drop into the "(No preset)" state. The workspace keeps its current
        // settings — we don't mutate the user's values just because the preset
        // they were tracking was deleted.
        SelectedPreset = null;
        IsPresetDirty = false;
    }

    [RelayCommand(CanExecute = nameof(CanRevert))]
    private void RevertPreset()
    {
        if (SelectedPreset is null || !IsPresetDirty)
            return;

        // Re-apply the preset's saved settings. This flows through the workspace
        // and back via OutputSettingsChangedMessage → SyncWithSettings, which will
        // clear the dirty flag once values match.
        PresetApplied?.Invoke(SelectedPreset.Settings);
    }

    private bool CanRevert() => SelectedPreset is not null && IsPresetDirty;
}
