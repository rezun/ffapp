using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class CommandPreviewViewModel : ObservableObject
{
    private readonly ICommandBuilder _commandBuilder;
    private readonly IDialogService? _dialogService;
    private JobContext? _context;

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
        _dialogService = dialogService;
    }

    public void UpdateJobContext(JobContext? context)
    {
        _context = context;
        Refresh();
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
        if (_context is null)
        {
            CommandText = "Load a source file to preview commands.";
            _commands = null;
            HasCommands = false;
            return;
        }

        try
        {
            var job = FormatHelpers.BuildJobDefinition(_context);
            _commands = _commandBuilder.Build(job, _context.MediaInfo);
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
}
