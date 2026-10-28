using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Services;

namespace FFApp.ViewModels;

/// <summary>
/// Output card state. The output location is a rule (next to each source, or one custom
/// folder) rather than a fixed path, and file names are always generated per source.
/// </summary>
public partial class OutputPathViewModel : ObservableObject
{
    private readonly IDialogService _dialogService;
    private string _sourcePath = "";
    private OutputContainer _container = OutputContainer.Mp4;
    private string? _presetName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomFolder))]
    private OutputLocationMode _locationMode = OutputLocationMode.SourceFolder;

    [ObservableProperty]
    private string _customFolder = "";

    [ObservableProperty]
    private string _resolvedPath = "";

    [ObservableProperty]
    private bool _hasSource;

    [ObservableProperty]
    private OutputContainer _selectedContainer = OutputContainer.Mp4;

    [ObservableProperty]
    private IReadOnlyList<OutputContainer> _availableContainers =
        Core.Validation.CompatibilityData.VideoContainers;

    public OutputPathViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public bool IsCustomFolder => LocationMode == OutputLocationMode.CustomFolder;

    /// <summary>Name of the selected saved preset, appended to generated file names.</summary>
    public string? PresetName
    {
        get => _presetName;
        private set => SetProperty(ref _presetName, value);
    }

    public OutputLocation Location => new(
        LocationMode,
        string.IsNullOrWhiteSpace(CustomFolder) ? null : CustomFolder);

    public void RestoreLocation(OutputLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        CustomFolder = location.CustomFolder ?? "";
        LocationMode = location.Mode;
    }

    public void UpdateFromSource(SourceMediaInfo mediaInfo)
    {
        _sourcePath = mediaInfo.SourcePath;
        HasSource = true;
        UpdateResolvedPath();
    }

    public void UpdateContainer(OutputContainer container)
    {
        _container = container;
        SelectedContainer = container;
        UpdateResolvedPath();
    }

    public void UpdateAvailableContainers(IReadOnlyList<OutputContainer> containers)
    {
        AvailableContainers = containers;
    }

    partial void OnSelectedContainerChanged(OutputContainer value)
    {
        // Sync internal state when user changes container in the Output card
        _container = value;
        UpdateResolvedPath();
    }

    public void UpdatePresetName(string? presetName)
    {
        PresetName = presetName;
        UpdateResolvedPath();
    }

    /// <summary>
    /// Returns the custom output directory, or null to use each source file's directory.
    /// </summary>
    public string? GetEffectiveDirectory() => Location.EffectiveDirectory;

    partial void OnLocationModeChanged(OutputLocationMode value) => UpdateResolvedPath();
    partial void OnCustomFolderChanged(string value) => UpdateResolvedPath();

    [RelayCommand]
    private async Task BrowseDirectoryAsync()
    {
        var folder = await _dialogService.OpenFolderAsync("Select output folder");
        if (folder is null)
            return;

        CustomFolder = folder;
        LocationMode = OutputLocationMode.CustomFolder;
    }

    private void UpdateResolvedPath()
    {
        if (string.IsNullOrEmpty(_sourcePath))
        {
            ResolvedPath = "";
            return;
        }

        try
        {
            ResolvedPath = DefaultCommandBuilder.SuggestOutputPath(
                _sourcePath, _container,
                GetEffectiveDirectory(),
                OutputNaming.AutoStem(_sourcePath, PresetName),
                isAutoName: true);
        }
        catch (Exception ex) when (ex is CommandBuildException or ArgumentException or IOException)
        {
            ResolvedPath = "";
        }
    }
}
