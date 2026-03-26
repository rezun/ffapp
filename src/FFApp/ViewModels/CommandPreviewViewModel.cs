using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class CommandPreviewViewModel : ObservableObject
{
    private readonly ICommandBuilder _commandBuilder;
    private readonly IValidationService _validationService;
    private readonly IDialogService? _dialogService;
    private OutputSettings? _currentSettings;
    private SourceMediaInfo? _currentMediaInfo;
    private string _ffmpegPath = "ffmpeg";

    [ObservableProperty]
    private string _commandText = "";

    [ObservableProperty]
    private bool _hasCommands;

    private IReadOnlyList<GeneratedCommand>? _commands;

    public CommandPreviewViewModel(
        ICommandBuilder commandBuilder,
        IValidationService validationService,
        IDialogService? dialogService = null)
    {
        _commandBuilder = commandBuilder;
        _validationService = validationService;
        _dialogService = dialogService;
    }

    public void SetFfmpegPath(string path)
    {
        _ffmpegPath = path;
        Refresh();
    }

    public void UpdateSourceInfo(SourceMediaInfo mediaInfo)
    {
        _currentMediaInfo = mediaInfo;
        Refresh();
    }

    public void UpdateOutputSettings(OutputSettings settings)
    {
        _currentSettings = settings;
        Refresh();
    }

    public void UpdatePreview(
        OutputSettings settings,
        SourceMediaInfo? mediaInfo,
        ICommandBuilder builder,
        string ffmpegPath)
    {
        _currentSettings = settings;
        _currentMediaInfo = mediaInfo;
        _ffmpegPath = ffmpegPath;

        if (mediaInfo is null)
        {
            CommandText = "Load a source file to preview commands.";
            _commands = null;
            HasCommands = false;
            return;
        }

        try
        {
            var job = CreateJobDefinition(settings, mediaInfo);
            _commands = builder.Build(job, mediaInfo);
            CommandText = string.Join(
                Environment.NewLine + Environment.NewLine,
                _commands.Select(c => c.DisplayString));
            HasCommands = _commands.Count > 0;
        }
        catch (Exception ex)
        {
            CommandText = $"Error: {ex.Message}";
            _commands = null;
            HasCommands = false;
        }
    }

    [RelayCommand]
    private async Task CopyCommandAsync()
    {
        if (_dialogService is not null && !string.IsNullOrEmpty(CommandText))
        {
            await _dialogService.ShowMessageAsync("Copied", "Command copied to clipboard.");
        }
    }

    private void Refresh()
    {
        if (_currentSettings is null)
            return;

        UpdatePreview(_currentSettings, _currentMediaInfo, _commandBuilder, _ffmpegPath);
    }

    private static JobDefinition CreateJobDefinition(OutputSettings settings, SourceMediaInfo mediaInfo)
    {
        var outputPath = DefaultCommandBuilder.SuggestOutputPath(
            mediaInfo.SourcePath, settings.Container);

        return FormatHelpers.BuildJobDefinition(mediaInfo.SourcePath, outputPath, settings);
    }
}
