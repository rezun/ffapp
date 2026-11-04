using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFRux.Core;
using FFRux.Core.Models;
using FFRux.Services;

namespace FFRux.ViewModels;

/// <summary>An entry in the format dropdown, with the number of problems the current tracks would have.</summary>
public sealed partial class FormatOptionViewModel(OutputFormatInfo info) : ObservableObject
{
    public OutputFormatInfo Info { get; } = info;
    public OutputFormat Format => Info.Format;
    public string Label => Info.Label;

    [ObservableProperty] private string _problemText = "";
    [ObservableProperty] private bool _hasErrors;
}

/// <summary>The output card: format, container title and file path.</summary>
public sealed partial class OutputViewModel : ObservableObject
{
    private readonly IDialogService _dialogs;
    private IReadOnlyList<string> _sourcePaths = [];
    private bool _pathIsCustom;
    private bool _titleIsCustom;
    private bool _suppressCustom;

    public OutputViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        Formats = [.. OutputFormats.All.Select(info => new FormatOptionViewModel(info))];
        _selectedFormat = Formats[0];
    }

    public event EventHandler? Changed;

    public ObservableCollection<FormatOptionViewModel> Formats { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Format), nameof(FormatInfo), nameof(TitleEnabled), nameof(TitleHint), nameof(StatisticsVisible))]
    private FormatOptionViewModel _selectedFormat;

    [ObservableProperty] private string _title = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseCommand))]
    private string _outputPath = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BrowseCommand))]
    private bool _isEditable = true;

    /// <summary>Runs mkvpropedit after a Matroska remux to rewrite BPS, duration, frame and byte statistics.</summary>
    [ObservableProperty] private bool _updateStatistics = true;

    /// <summary>Whether mkvpropedit was found; without it the option has no effect.</summary>
    public bool MkvpropeditAvailable { get; set; } = true;

    public OutputFormat Format => SelectedFormat.Format;
    public OutputFormatInfo FormatInfo => SelectedFormat.Info;
    public bool TitleEnabled => FormatInfo.SupportsTitle;
    public string? TitleHint => TitleEnabled ? null : $"{FormatInfo.Name} can't store a title.";
    public bool HasSources => _sourcePaths.Count > 0;
    public bool StatisticsVisible => FormatInfo.IsMatroska;

    public void SelectFormat(OutputFormat format) =>
        SelectedFormat = Formats.First(option => option.Format == format);

    /// <summary>
    /// Updates the defaults when sources change: the path next to the first source and the title of the
    /// first source that has one. Values the user typed are kept.
    /// </summary>
    public void UpdateSources(IReadOnlyList<string> sourcePaths, string? firstTitle)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        _sourcePaths = sourcePaths;
        OnPropertyChanged(nameof(HasSources));
        BrowseCommand.NotifyCanExecuteChanged();

        _suppressCustom = true;
        try
        {
            if (sourcePaths.Count == 0)
            {
                OutputPath = "";
                Title = "";
                _pathIsCustom = false;
                _titleIsCustom = false;
                return;
            }
            if (!_pathIsCustom)
                OutputPath = OutputPaths.Suggest(sourcePaths[0], Format, sourcePaths);
            if (!_titleIsCustom)
                Title = firstTitle ?? "";
        }
        finally
        {
            _suppressCustom = false;
        }
    }

    /// <summary>Shows the problem count per format in the dropdown; the closed field shows only the name.</summary>
    public void UpdateProblemCounts(IReadOnlyDictionary<OutputFormat, (int Errors, int Warnings)> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        foreach (var option in Formats)
        {
            var (errors, warnings) = counts.GetValueOrDefault(option.Format);
            option.HasErrors = errors > 0;
            option.ProblemText = (errors, warnings) switch
            {
                (0, 0) => "",
                (0, 1) => "1 warning",
                (0, _) => string.Create(CultureInfo.InvariantCulture, $"{warnings} warnings"),
                (1, _) => "1 problem",
                _ => string.Create(CultureInfo.InvariantCulture, $"{errors} problems"),
            };
        }
    }

    [RelayCommand(CanExecute = nameof(CanBrowse))]
    private async Task BrowseAsync()
    {
        var suggested = OutputPath.Length > 0 ? OutputPath : OutputPaths.Suggest(_sourcePaths[0], Format, _sourcePaths);
        if (await _dialogs.PickOutputFileAsync(suggested, FormatInfo) is not { } chosen)
            return;

        // The file name decides nothing: the format stays as chosen and the extension follows it.
        OutputPath = OutputPaths.WithFormat(chosen, Format, _sourcePaths);
        _pathIsCustom = true;
    }

    private bool CanBrowse() => IsEditable && _sourcePaths.Count > 0;

    partial void OnSelectedFormatChanged(FormatOptionViewModel value)
    {
        if (OutputPath.Length > 0)
        {
            // A suggested path is suggested again, so "(remux)" disappears once the extension no longer collides.
            _suppressCustom = true;
            OutputPath = _pathIsCustom || _sourcePaths.Count == 0
                ? OutputPaths.WithFormat(OutputPath, value.Format, _sourcePaths)
                : OutputPaths.Suggest(_sourcePaths[0], value.Format, _sourcePaths);
            _suppressCustom = false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnUpdateStatisticsChanged(bool value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnTitleChanged(string value)
    {
        if (!_suppressCustom)
            _titleIsCustom = true;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnOutputPathChanged(string value)
    {
        if (!_suppressCustom)
            _pathIsCustom = value.Length > 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
