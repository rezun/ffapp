using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using Avalonia.Data.Converters;
using FFApp.Services;

namespace FFApp.ViewModels;

public partial class CommandPreviewViewModel : ObservableObject
{
    public static readonly FuncValueConverter<bool, string> ExpandCollapseConverter =
        new(expanded => expanded ? "\u25BC Collapse" : "\u25B6 Expand");

    private readonly ICommandBuilder _commandBuilder;
    private readonly IMatroskaPostProcessor? _matroskaPostProcessor;
    private readonly IDialogService? _dialogService;
    private JobContext? _context;

    [ObservableProperty]
    private string _commandText = "";

    [ObservableProperty]
    private bool _hasCommands;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _showCopiedIndicator;

    private CancellationTokenSource? _copiedCts;
    private IReadOnlyList<GeneratedCommand>? _commands;

    public CommandPreviewViewModel(
        ICommandBuilder commandBuilder,
        IDialogService? dialogService = null,
        IMatroskaPostProcessor? matroskaPostProcessor = null)
    {
        _commandBuilder = commandBuilder;
        _dialogService = dialogService;
        _matroskaPostProcessor = matroskaPostProcessor;
    }

    public void UpdateJobContext(JobContext? context)
    {
        _context = context;
        Refresh();
    }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private async Task CopyCommandAsync()
    {
        if (_dialogService is null || string.IsNullOrEmpty(CommandText))
            return;

        await CopyAndFlashAsync(CommandText);
    }

    [RelayCommand]
    private async Task CopyTemplateAsync()
    {
        if (_dialogService is null || _context is null || _commands is null || _commands.Count == 0)
            return;

        var sourcePath = _context.MediaInfo.SourcePath;
        var outputPath = FormatHelpers.ResolveOutputPath(_context);
        var ext = DefaultCommandBuilder.ContainerExtension(_context.Settings.Container);

        var templateText = string.Join(
            Environment.NewLine + Environment.NewLine,
            _commands.Select(c => ReplacePathsWithTemplate(
                c.DisplayString, c.ExecutablePath, sourcePath, outputPath, ext)));

        await CopyAndFlashAsync(templateText);
    }

    private async Task CopyAndFlashAsync(string text)
    {
        await _dialogService!.SetClipboardTextAsync(text);

        _copiedCts?.Cancel();
        _copiedCts = new CancellationTokenSource();
        ShowCopiedIndicator = true;

        try
        {
            await Task.Delay(2000, _copiedCts.Token);
            ShowCopiedIndicator = false;
        }
        catch (TaskCanceledException)
        {
            // Rapid re-click; the new call takes over.
        }
    }

    private static string ReplacePathsWithTemplate(
        string displayString, string executablePath, string sourcePath, string outputPath, string ext)
    {
        // The display string shell-quotes paths with single quotes when they
        // contain special characters, or leaves them bare. Replace both forms.
        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        var result = ReplacePath(displayString, executablePath, executableName);

        var inputPlaceholder = "\"$input\"";
        result = ReplacePath(result, sourcePath, inputPlaceholder);

        var outputPlaceholder = "\"${input%.*}-encoded" + ext + "\"";
        result = ReplacePath(result, outputPath, outputPlaceholder);

        return result;
    }

    private static string ReplacePath(string text, string path, string replacement)
    {
        var quoted = "'" + path.Replace("'", "'\"'\"'") + "'";
        return text.Contains(quoted)
            ? text.Replace(quoted, replacement)
            : text.Replace(path, replacement);
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
            var commands = _commandBuilder.Build(job, _context.MediaInfo).ToList();
            if (_matroskaPostProcessor?.BuildCommand(job) is { } postProcessCommand)
                commands.Add(postProcessCommand);
            _commands = commands;
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
