using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class OutputPathViewModel : ObservableObject
{
    private readonly IDialogService _dialogService;
    private string _sourcePath = "";
    private string _sourceStem = "";
    private string _sourceDirectory = "";
    private string? _presetName;
    private OutputContainer _container = OutputContainer.Mp4;
    private bool _hasInitializedDirectory;

    [ObservableProperty]
    private string _outputDirectory = "";

    [ObservableProperty]
    private string _outputFilename = "";

    [ObservableProperty]
    private bool _isAutoName = true;

    [ObservableProperty]
    private string _extension = ".mp4";

    [ObservableProperty]
    private string _resolvedPath = "";

    [ObservableProperty]
    private bool _hasSource;

    public OutputPathViewModel(IDialogService dialogService)
    {
        _dialogService = dialogService;
    }

    public void UpdateFromSource(SourceMediaInfo mediaInfo)
    {
        _sourcePath = mediaInfo.SourcePath;
        _sourceStem = Path.GetFileNameWithoutExtension(mediaInfo.SourcePath);
        _sourceDirectory = Path.GetDirectoryName(mediaInfo.SourcePath) ?? "";

        if (!_hasInitializedDirectory)
        {
            OutputDirectory = _sourceDirectory;
            _hasInitializedDirectory = true;
        }

        if (IsAutoName)
            OutputFilename = BuildAutoFilename();

        HasSource = true;
        UpdateResolvedPath();
    }

    public void UpdateContainer(OutputContainer container)
    {
        _container = container;
        Extension = DefaultCommandBuilder.ContainerExtension(container);
        UpdateResolvedPath();
    }

    public void UpdatePresetName(string? presetName)
    {
        _presetName = presetName;

        if (IsAutoName)
            OutputFilename = BuildAutoFilename();
    }

    /// <summary>
    /// Returns the configured output directory, or null to use the source file's directory.
    /// </summary>
    public string? GetEffectiveDirectory() =>
        string.IsNullOrWhiteSpace(OutputDirectory) ? null : OutputDirectory;

    /// <summary>
    /// Returns the configured output filename (without extension), or null to auto-generate from source.
    /// </summary>
    public string? GetEffectiveFilename() =>
        IsAutoName
            ? (_presetName is not null ? OutputFilename : null)
            : (string.IsNullOrWhiteSpace(OutputFilename) ? null : OutputFilename);

    partial void OnOutputDirectoryChanged(string value) => UpdateResolvedPath();
    partial void OnOutputFilenameChanged(string value) => UpdateResolvedPath();
    partial void OnExtensionChanged(string value) => UpdateResolvedPath();

    partial void OnIsAutoNameChanged(bool value)
    {
        if (value)
            OutputFilename = BuildAutoFilename();

        UpdateResolvedPath();
    }

    [RelayCommand]
    private async Task BrowseDirectoryAsync()
    {
        var folder = await _dialogService.OpenFolderAsync("Select output folder");
        if (folder is not null)
            OutputDirectory = folder;
    }

    private string BuildAutoFilename() =>
        string.IsNullOrEmpty(_presetName) ? _sourceStem : $"{_sourceStem} - {_presetName}";

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
                GetEffectiveDirectory(), GetEffectiveFilename(),
                IsAutoName);
        }
        catch
        {
            ResolvedPath = "";
        }
    }
}
