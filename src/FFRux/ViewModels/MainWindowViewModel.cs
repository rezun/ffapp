using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Infrastructure;
using FFRux.Core;
using FFRux.Core.Models;
using FFRux.Infrastructure;
using FFRux.Services;

namespace FFRux.ViewModels;

/// <summary>A chapter choice: one per source with chapters, plus "No chapters".</summary>
public sealed record ChapterOptionViewModel(SourceViewModel? Source, string Label)
{
    public int? SourceNumber => Source?.Number;
    public int MarkerIndex => Source?.MarkerIndex ?? 0;
    public bool HasMarker => Source is not null;
}

public sealed record IssueViewModel(IssueSeverity Severity, string Message)
{
    public bool IsError => Severity == IssueSeverity.Error;
    public bool IsWarning => Severity == IssueSeverity.Warning;
    public bool IsInfo => Severity == IssueSeverity.Info;

    public string Symbol => Severity switch
    {
        IssueSeverity.Error => "✕",
        IssueSeverity.Warning => "▲",
        IssueSeverity.Info => "ℹ",
        _ => throw new ArgumentOutOfRangeException(nameof(Severity), Severity, null),
    };
}

/// <summary>
/// Coordinates the window: sources, the track table, chapters, the output card and the job. Every
/// change rebuilds the <see cref="RemuxPlan"/> and runs the compatibility check again.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncInitializable
{
    private readonly IDependencyService _dependencies;
    private readonly ExecutablePathsConfig _pathsConfig;
    private readonly IMediaProbeService _probe;
    private readonly IDialogService _dialogs;
    private readonly ISettingsService _settings;
    private readonly IRemuxJobRunner _runner;
    private readonly RemuxCommandBuilder _commandBuilder;
    private readonly ILogger _logger;

    private readonly Dictionary<TrackKind, List<TrackViewModel>> _groups = new()
    {
        [TrackKind.Video] = [],
        [TrackKind.Audio] = [],
        [TrackKind.Subtitle] = [],
    };
    private readonly Dictionary<TrackKind, TrackGroupViewModel> _headers = new()
    {
        [TrackKind.Video] = new(TrackKind.Video),
        [TrackKind.Audio] = new(TrackKind.Audio),
        [TrackKind.Subtitle] = new(TrackKind.Subtitle),
    };

    private Task? _initialization;
    private CancellationTokenSource? _jobCancellation;
    private Task? _job;
    private bool _chapterChoiceIsCustom;
    private bool _updatingChapters;
    private bool _mkvpropeditAvailable;

    public MainWindowViewModel(
        IDependencyService dependencies,
        ExecutablePathsConfig pathsConfig,
        IMediaProbeService probe,
        IDialogService dialogs,
        ISettingsService settings,
        IRemuxJobRunner runner,
        RemuxCommandBuilder commandBuilder,
        OutputViewModel output,
        TrackPropertiesViewModel properties,
        CommandPreviewViewModel commandPreview,
        ILogger<MainWindowViewModel>? logger = null)
    {
        _logger = logger ?? NullLogger<MainWindowViewModel>.Instance;
        _dependencies = dependencies;
        _pathsConfig = pathsConfig;
        _probe = probe;
        _dialogs = dialogs;
        _settings = settings;
        _runner = runner;
        _commandBuilder = commandBuilder;
        Output = output;
        Properties = properties;
        CommandPreview = commandPreview;
        Output.Changed += (_, _) => OnUserChange();
        Output.PropertyChanged += (_, e) =>
        {
            // PropertyChanged is synchronous, so remembering the format runs fire-and-forget;
            // SaveFormatAsync catches and logs its own failures.
            if (e.PropertyName == nameof(OutputViewModel.SelectedFormat) && _initialization?.IsCompleted == true)
                _ = SaveFormatAsync(Output.Format);
        };
    }

    public OutputViewModel Output { get; }
    public TrackPropertiesViewModel Properties { get; }
    public CommandPreviewViewModel CommandPreview { get; }

    public ObservableCollection<SourceViewModel> Sources { get; } = [];

    /// <summary>The track table: a header row per group followed by its tracks, in output order.</summary>
    public ObservableCollection<object> Rows { get; } = [];

    public ObservableCollection<ChapterOptionViewModel> ChapterOptions { get; } = [];
    public ObservableCollection<IssueViewModel> Issues { get; } = [];

    /// <summary>All tracks in output order.</summary>
    public IReadOnlyList<TrackViewModel> Tracks =>
        [.. _groups[TrackKind.Video], .. _groups[TrackKind.Audio], .. _groups[TrackKind.Subtitle]];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddSourcesCommand))]
    private bool _isReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    [NotifyCanExecuteChangedFor(nameof(AddSourcesCommand), nameof(RemoveSourceCommand), nameof(RemoveAllSourcesCommand),
        nameof(StartCommand), nameof(CancelCommand), nameof(MoveSelectedUpCommand), nameof(MoveSelectedDownCommand),
        nameof(ToggleSelectedTracksCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSources), nameof(IsEmpty))]
    [NotifyCanExecuteChangedFor(nameof(RemoveAllSourcesCommand))]
    private int _sourceCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveSourceCommand))]
    private SourceViewModel? _selectedSource;

    [ObservableProperty] private ChapterOptionViewModel? _selectedChapter;
    [ObservableProperty] private bool _hasChapters;
    [ObservableProperty] private string _windowTitle = "FFRux";
    [ObservableProperty] private string _tracksSummary = "";
    [ObservableProperty] private string _statusColumnHeader = "Status";
    [ObservableProperty] private string _issuesSummary = "";
    [ObservableProperty] private string? _allFitText;
    [ObservableProperty] private bool _hasIssues;
    [ObservableProperty] private string? _toolsMessage;

    // Status bar
    [ObservableProperty] private string _statusMessage = "Looking for FFmpeg…";
    [ObservableProperty] private IssueSeverity? _statusSeverity;
    [ObservableProperty] private string _estimatedSizeText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _canStart;

    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private bool _progressIsIndeterminate;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _remainingText = "";

    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private bool _resultSucceeded;
    [ObservableProperty] private string _resultText = "";
    [ObservableProperty] private string? _resultDetail;
    [ObservableProperty] private string? _resultPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResultLog))]
    [NotifyCanExecuteChangedFor(nameof(OpenLogFileCommand))]
    private string? _resultLogPath;

    public bool HasResultLog => ResultLogPath is not null;

    public bool IsEditable => !IsRunning;
    public bool HasSources => SourceCount > 0;
    public bool IsEmpty => SourceCount == 0;
    public bool IsStatusError => StatusSeverity == IssueSeverity.Error;
    public bool IsStatusWarning => StatusSeverity == IssueSeverity.Warning;
    public bool IsStatusOk => StatusSeverity == IssueSeverity.Info;

    /// <summary>Discovers FFmpeg and loads settings once. Files opened before that finishes wait for it.</summary>
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();

    public async Task OpenFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);
        await InitializeAsync();
        if (IsRunning)
            return;

        foreach (var candidate in paths)
        {
            var path = ResolveVobSub(Path.GetFullPath(candidate));
            if (Sources.Any(source => CompatibilityChecker.PathsEqual(source.Path, path)))
                continue;

            var source = await ProbeAsync(path, ct);
            AddSource(source);
        }
        AfterSourcesChanged();
    }

    /// <summary>Moves a track to the position of another track in the same group (drag and drop).</summary>
    public void MoveTrack(TrackViewModel track, TrackViewModel target)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(target);
        if (IsRunning || track == target || track.Kind != target.Kind)
            return;

        var group = _groups[track.Kind];
        var targetIndex = group.IndexOf(target);
        group.Remove(track);
        group.Insert(targetIndex, track);
        SyncRows();
        OnUserChange();
    }

    /// <summary>Called by the view when the table selection changes.</summary>
    public void SelectTracks(IReadOnlyList<TrackViewModel> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        Properties.Select(tracks, Output.FormatInfo);
        MoveSelectedUpCommand.NotifyCanExecuteChanged();
        MoveSelectedDownCommand.NotifyCanExecuteChanged();
        ToggleSelectedTracksCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanAddSources))]
    private async Task AddSourcesAsync()
    {
        var paths = await _dialogs.PickSourceFilesAsync();
        if (paths.Count > 0)
            await OpenFilesAsync(paths);
    }

    private bool CanAddSources() => IsReady && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRemoveSource))]
    private void RemoveSource()
    {
        if (SelectedSource is not { } source)
            return;
        foreach (var group in _groups.Values)
            group.RemoveAll(track => track.SourceFile == source);
        Sources.Remove(source);
        AfterSourcesChanged();
    }

    private bool CanRemoveSource() => SelectedSource is not null && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRemoveAll))]
    private void RemoveAllSources()
    {
        foreach (var group in _groups.Values)
            group.Clear();
        Sources.Clear();
        AfterSourcesChanged();
    }

    private bool CanRemoveAll() => SourceCount > 0 && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanMoveSelected))]
    private void MoveSelectedUp() => MoveSelected(-1);

    [RelayCommand(CanExecute = nameof(CanMoveSelected))]
    private void MoveSelectedDown() => MoveSelected(1);

    private bool CanMoveSelected() => !IsRunning && Properties.Single is not null;

    private void MoveSelected(int direction)
    {
        if (Properties.Single is not { } track)
            return;
        var group = _groups[track.Kind];
        var index = group.IndexOf(track) + direction;
        if (index < 0 || index >= group.Count)
            return;
        MoveTrack(track, group[index]);
    }

    [RelayCommand(CanExecute = nameof(CanToggleSelected))]
    private void ToggleSelectedTracks() => TrackViewModel.ToggleEnabled(Properties.Tracks);

    private bool CanToggleSelected() => !IsRunning && Properties.HasSelection;

    [RelayCommand(CanExecute = nameof(CanStartJob))]
    private async Task StartAsync()
    {
        Refresh();
        if (!CanStart || BuildPlan() is not { OutputPath: { } outputPath } plan)
            return;

        if (File.Exists(outputPath)
            && !await _dialogs.ConfirmAsync("Replace file?",
                $"{Path.GetFileName(outputPath)} already exists. Do you want to replace it?", "Replace"))
        {
            return;
        }

        HasResult = false;
        CommandPreview.ClearLog();
        ProgressPercent = 0;
        ProgressIsIndeterminate = true;
        ProgressText = "Testing the combination…";
        RemainingText = "";
        IsRunning = true;
        UpdateEditable();

        using var cancellation = new CancellationTokenSource();
        _jobCancellation = cancellation;
        // Progress<T> captures the UI context here, so callbacks from FFmpeg's threads arrive on the UI thread.
        var progress = new Progress<RemuxProgress>(ShowProgress);
        var log = new Progress<string>(CommandPreview.AppendLog);
        try
        {
            var run = _runner.RunAsync(plan, ((IProgress<RemuxProgress>)progress).Report, ((IProgress<string>)log).Report, cancellation.Token);
            _job = run;
            ShowResult(await run);
        }
        finally
        {
            _job = null;
            _jobCancellation = null;
            IsRunning = false;
            UpdateEditable();
            Refresh();
        }
    }

    private bool CanStartJob() => CanStart && !IsRunning;

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task CancelAsync()
    {
        if (_jobCancellation is { } cancellation)
            await cancellation.CancelAsync();
    }

    /// <summary>
    /// Asks before closing while a job runs. When the user agrees, the job is canceled and awaited,
    /// so FFmpeg does not keep running and the partial file is deleted.
    /// </summary>
    public async Task<bool> PrepareToCloseAsync()
    {
        if (!IsRunning)
            return true;
        if (!await _dialogs.ConfirmAsync("Cancel remux?", "A remux is running. Cancel it and quit FFRux?", "Cancel and quit"))
            return false;

        if (_jobCancellation is { } cancellation)
            await cancellation.CancelAsync();
        if (_job is { } job)
            await job;
        return true;
    }

    [RelayCommand]
    private async Task ShowInFolderAsync()
    {
        if (ResultPath is { } path)
            await _dialogs.ShowInFolderAsync(path);
    }

    [RelayCommand]
    private void ShowLog() => CommandPreview.ShowLogExpanded();

    [RelayCommand(CanExecute = nameof(HasResultLog))]
    private async Task OpenLogFileAsync()
    {
        if (ResultLogPath is { } path)
            await _dialogs.OpenWithDefaultAppAsync(path);
    }

    [RelayCommand]
    private void DismissResult()
    {
        HasResult = false;
        Refresh();
    }

    private async Task InitializeCoreAsync()
    {
        var settings = await _settings.LoadAsync();
        if (settings.LastFormat is { } format)
            Output.SelectFormat(format);

        // FFRux has no paths to configure, so discovery relies on PATH and the usual install locations.
        var status = await _dependencies.DiscoverAsync(ExecutablePaths.Empty);
        _pathsConfig.Configure(status.ExecutablePaths);
        _mkvpropeditAvailable = status.Mkvpropedit.IsValid;
        IsReady = status.IsReady;
        ToolsMessage = status.IsReady
            ? null
            : "FFmpeg and FFprobe were not found. Install them or add them to PATH, then restart FFRux.";
        Refresh();
    }

    private async Task<SourceViewModel> ProbeAsync(string path, CancellationToken ct)
    {
        if (!IsReady)
            return SourceViewModel.Failed(path, "Can't read this file without FFprobe.");

        try
        {
            var info = await _probe.ProbeAsync(path, ct);
            var isStandaloneText = IsStandaloneTextSubtitle(info);
            var isUnicode = !isStandaloneText || await IsUnicodeFileAsync(path, ct);
            return SourceViewModel.Probed(path, info, isStandaloneText, isUnicode);
        }
        catch (MediaProbeException ex)
        {
            return SourceViewModel.Failed(path, ex.Message);
        }
    }

    private static bool IsStandaloneTextSubtitle(SourceMediaInfo info) =>
        info.StreamCount == 1
        && info.SubtitleStreams.Length == 1
        && info.SubtitleStreams[0].CodecName is { } codec
        && !Codecs.IsBitmapSubtitle(codec)
        && info.ContainerFormat?.StartsWith("matroska", StringComparison.Ordinal) != true
        && info.ContainerFormat?.StartsWith("mov", StringComparison.Ordinal) != true;

    private static async Task<bool> IsUnicodeFileAsync(string path, CancellationToken ct)
    {
        const long maxBytes = 32 * 1024 * 1024;
        try
        {
            if (new FileInfo(path).Length > maxBytes)
                return true;
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return TextEncodings.IsUnicode(bytes);
        }
        catch (IOException)
        {
            // FFmpeg reports an unreadable file when the job runs; assume UTF-8 until then.
            return true;
        }
    }

    // FFmpeg opens VobSub through the .idx file and finds the .sub next to it.
    private static string ResolveVobSub(string path)
    {
        if (!Path.GetExtension(path).Equals(".sub", StringComparison.OrdinalIgnoreCase))
            return path;
        var idx = Path.ChangeExtension(path, ".idx");
        return File.Exists(idx) ? idx : path;
    }

    private void AddSource(SourceViewModel source)
    {
        source.Number = Sources.Count + 1;
        source.Changed += (_, _) => OnUserChange();
        Sources.Add(source);
        if (source.MediaInfo is not { } info)
            return;

        var tracks = SourceTracks.Create(source.Number, info);
        var hint = tracks.Count == 1 ? source.Hint : FileNameHint.None;
        foreach (var track in tracks)
        {
            var row = new TrackViewModel(source, track, SourceTracks.InitialSettings(track, hint))
            {
                IsEditable = IsEditable,
            };
            row.Changed += OnTrackChanged;
            source.AddTrack(row);
            _groups[track.Kind].Add(row);
        }
    }

    private void OnTrackChanged(object? sender, EventArgs e) => OnUserChange();

    // A result in the status bar describes the last job; any edit makes the readiness status relevant again.
    private void OnUserChange()
    {
        HasResult = false;
        Refresh();
    }

    private void AfterSourcesChanged()
    {
        for (var i = 0; i < Sources.Count; i++)
            Sources[i].Number = i + 1;
        SourceCount = Sources.Count;
        if (SelectedSource is not null && !Sources.Contains(SelectedSource))
            SelectedSource = null;

        HasResult = false;
        UpdateChapterOptions();
        var firstTitle = Sources.Select(source => source.MediaInfo?.Title).FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
        Output.UpdateSources([.. Sources.Select(source => source.Path)], firstTitle);
        SyncRows();

        var remaining = Properties.Tracks.Where(track => Tracks.Contains(track)).ToList();
        if (remaining.Count != Properties.Tracks.Count)
            SelectTracks(remaining);
        Refresh();
    }

    private void UpdateChapterOptions()
    {
        var previous = SelectedChapter?.Source;
        _updatingChapters = true;
        try
        {
            ChapterOptions.Clear();
            foreach (var source in Sources.Where(source => source.Chapters.Count > 0))
            {
                var count = source.Chapters.Count;
                ChapterOptions.Add(new ChapterOptionViewModel(source, count == 1
                    ? "1 chapter"
                    : string.Create(CultureInfo.InvariantCulture, $"{count} chapters")));
            }
            if (ChapterOptions.Count > 0)
                ChapterOptions.Add(new ChapterOptionViewModel(null, "No chapters"));

            ChapterOptionViewModel? selection;
            if (_chapterChoiceIsCustom && previous is null && ChapterOptions.Count > 0)
                selection = ChapterOptions[^1];
            else if (_chapterChoiceIsCustom && ChapterOptions.FirstOrDefault(option => option.Source == previous) is { } kept)
                selection = kept;
            else
                selection = ChapterOptions.FirstOrDefault();
            SelectedChapter = selection;
            HasChapters = ChapterOptions.Count > 0;
            if (ChapterOptions.Count == 0)
                _chapterChoiceIsCustom = false;
        }
        finally
        {
            _updatingChapters = false;
        }
    }

    partial void OnSelectedChapterChanged(ChapterOptionViewModel? value)
    {
        if (_updatingChapters)
            return;
        _chapterChoiceIsCustom = true;
        OnUserChange();
    }

    partial void OnStatusSeverityChanged(IssueSeverity? value)
    {
        OnPropertyChanged(nameof(IsStatusError));
        OnPropertyChanged(nameof(IsStatusWarning));
        OnPropertyChanged(nameof(IsStatusOk));
    }

    /// <summary>Brings the rows in line with the groups using moves, so the table keeps its selection.</summary>
    private void SyncRows()
    {
        var desired = new List<object>();
        foreach (var (kind, group) in _groups)
        {
            if (group.Count == 0)
                continue;
            var header = _headers[kind];
            var enabled = group.Count(track => track.Enabled);
            header.CountText = string.Create(CultureInfo.InvariantCulture, $"{enabled} of {group.Count}");
            desired.Add(header);
            desired.AddRange(group);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < Rows.Count && ReferenceEquals(Rows[i], desired[i]))
                continue;
            var existing = IndexOf(Rows, desired[i], i + 1);
            if (existing >= 0)
                Rows.Move(existing, i);
            else
                Rows.Insert(i, desired[i]);
        }
        while (Rows.Count > desired.Count)
            Rows.RemoveAt(Rows.Count - 1);
    }

    private static int IndexOf(IList<object> list, object item, int start)
    {
        for (var i = start; i < list.Count; i++)
        {
            if (ReferenceEquals(list[i], item))
                return i;
        }
        return -1;
    }

    private RemuxPlan? BuildPlan(OutputFormat? format = null)
    {
        var sources = Sources.Select(source => source.ToModel()).OfType<RemuxSource>().ToList();
        if (sources.Count == 0)
            return null;
        return new RemuxPlan
        {
            Sources = sources,
            Tracks = [.. Tracks.Select(track => track.ToModel())],
            ChapterSourceNumber = SelectedChapter?.SourceNumber,
            Format = format ?? Output.Format,
            Title = Output.Title,
            OutputPath = string.IsNullOrWhiteSpace(Output.OutputPath) ? null : Output.OutputPath,
        };
    }

    /// <summary>Runs the compatibility check and updates statuses, issues, counts, command and status bar.</summary>
    private void Refresh()
    {
        var format = Output.FormatInfo;
        StatusColumnHeader = $"Status for {format.ShortName}";
        var plan = BuildPlan();
        foreach (var header in _headers.Values)
        {
            var group = _groups[header.Kind];
            header.CountText = string.Create(CultureInfo.InvariantCulture, $"{group.Count(track => track.Enabled)} of {group.Count}");
        }

        if (plan is null)
        {
            foreach (var track in Tracks)
                track.ApplyStatus(null);
            Issues.Clear();
            HasIssues = false;
            IssuesSummary = "";
            AllFitText = null;
            TracksSummary = "";
            EstimatedSizeText = "";
            CommandPreview.CommandText = "";
            Output.UpdateProblemCounts(new Dictionary<OutputFormat, (int, int)>());
            WindowTitle = "FFRux";
            CanStart = false;
            ShowStatus();
            Properties.Refresh(format);
            return;
        }

        var report = CompatibilityChecker.Check(plan);
        foreach (var track in Tracks)
            track.ApplyStatus(report.For(track.Key));

        var issues = report.Issues.Concat(CompatibilityChecker.CheckOutputPath(plan)).ToList();
        if (format.IsMatroska && !_mkvpropeditAvailable && IsReady)
            issues.Add(new CompatibilityIssue(IssueSeverity.Info, "mkvpropedit was not found, so the track statistics in the output are not updated."));
        issues.AddRange(Sources.Where(source => source.IsError)
            .Select(source => new CompatibilityIssue(IssueSeverity.Info, $"Source {source.Number} ({source.FileName}) can't be read and adds no tracks.")));
        issues.Sort((a, b) => b.Severity.CompareTo(a.Severity));

        Issues.Clear();
        foreach (var issue in issues)
            Issues.Add(new IssueViewModel(issue.Severity, issue.Message));
        HasIssues = Issues.Count > 0;

        var errors = issues.Count(issue => issue.Severity == IssueSeverity.Error);
        var warnings = issues.Count(issue => issue.Severity == IssueSeverity.Warning);
        var blockingTracks = report.Tracks.Values.Count(status => status.Level == CompatibilityLevel.Incompatible);
        IssuesSummary = Summarize(errors, warnings, blockingTracks);
        AllFitText = errors == 0 && warnings == 0 ? $"All enabled tracks fit {format.Name}" : null;

        var counts = new Dictionary<OutputFormat, (int, int)>();
        foreach (var option in OutputFormats.All)
        {
            var check = option.Format == plan.Format ? report : CompatibilityChecker.Check(plan with { Format = option.Format });
            counts[option.Format] = (check.ErrorCount, check.WarningCount);
        }
        Output.UpdateProblemCounts(counts);

        var total = Tracks.Count();
        var enabledCount = Tracks.Count(track => track.Enabled);
        TracksSummary = string.Create(CultureInfo.InvariantCulture, $"{enabledCount} of {total} enabled · drag rows to reorder");

        var estimate = SizeEstimator.Estimate(plan);
        EstimatedSizeText = estimate.Bytes > 0
            ? $"Estimated size {(estimate.IsComplete ? "" : "at least ")}{SizeEstimator.Format(estimate.Bytes)}"
            : "";

        WindowTitle = plan.OutputPath is { } output ? $"FFRux — {Path.GetFileName(output)}" : "FFRux";
        CommandPreview.CommandText = BuildCommandText(plan);
        CanStart = IsReady && errors == 0;
        ShowStatus(errors, warnings, blockingTracks, enabledCount);
        Properties.Refresh(format);
    }

    private string BuildCommandText(RemuxPlan plan)
    {
        if (plan.OutputPath is not { } output || plan.EnabledTracks.Count == 0)
            return "";
        var commands = _commandBuilder.Build(plan, output);
        return commands.PostProcess is { } post
            ? string.Concat(commands.Ffmpeg.DisplayString, Environment.NewLine, Environment.NewLine, post.DisplayString)
            : commands.Ffmpeg.DisplayString;
    }

    private static string Summarize(int errors, int warnings, int blockingTracks)
    {
        var parts = new List<string>();
        if (errors > 0)
            parts.Add(BlockingText(errors, blockingTracks));
        if (warnings > 0)
            parts.Add(warnings == 1 ? "1 warning" : string.Create(CultureInfo.InvariantCulture, $"{warnings} warnings"));
        return string.Join(" · ", parts);
    }

    private static string BlockingText(int errors, int blockingTracks) =>
        blockingTracks == errors
            ? blockingTracks == 1 ? "1 track blocks the start" : string.Create(CultureInfo.InvariantCulture, $"{blockingTracks} tracks block the start")
            : errors == 1 ? "1 problem blocks the start" : string.Create(CultureInfo.InvariantCulture, $"{errors} problems block the start");

    private void ShowStatus(int errors = 0, int warnings = 0, int blockingTracks = 0, int enabledCount = 0)
    {
        if (IsRunning || HasResult)
            return;
        if (ToolsMessage is { } tools)
        {
            StatusSeverity = IssueSeverity.Error;
            StatusMessage = tools;
        }
        else if (!IsReady)
        {
            StatusSeverity = null;
            StatusMessage = "Looking for FFmpeg…";
        }
        else if (SourceCount == 0)
        {
            StatusSeverity = null;
            StatusMessage = "Add source files to start.";
        }
        else if (errors > 0)
        {
            StatusSeverity = IssueSeverity.Error;
            StatusMessage = $"✕ {BlockingText(errors, blockingTracks)}";
        }
        else if (warnings > 0)
        {
            StatusSeverity = IssueSeverity.Warning;
            StatusMessage = warnings == 1
                ? "▲ Ready, with 1 warning"
                : string.Create(CultureInfo.InvariantCulture, $"▲ Ready, with {warnings} warnings");
        }
        else
        {
            StatusSeverity = IssueSeverity.Info;
            StatusMessage = enabledCount == 1
                ? $"✓ Ready to remux 1 track into {Output.FormatInfo.Name}"
                : string.Create(CultureInfo.InvariantCulture, $"✓ Ready to remux {enabledCount} tracks into {Output.FormatInfo.Name}");
        }
    }

    private void ShowProgress(RemuxProgress progress)
    {
        switch (progress.Stage)
        {
            case RemuxStage.Testing:
                ProgressIsIndeterminate = true;
                ProgressText = "Testing the combination…";
                RemainingText = "";
                break;
            case RemuxStage.Remuxing:
                ProgressIsIndeterminate = progress.Percentage is null;
                ProgressPercent = progress.Percentage ?? 0;
                ProgressText = progress.Percentage is { } percent
                    ? string.Create(CultureInfo.InvariantCulture, $"Remuxing… {percent:0} %")
                    : "Remuxing…";
                RemainingText = Remaining(progress);
                break;
            case RemuxStage.PostProcessing:
                ProgressIsIndeterminate = true;
                ProgressText = "Updating track statistics…";
                RemainingText = "";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(progress), progress.Stage, null);
        }
    }

    private static string Remaining(RemuxProgress progress)
    {
        if (progress.Percentage is not { } percent || percent < 1 || percent >= 100)
            return "";
        var remaining = TimeSpan.FromSeconds(progress.Elapsed.TotalSeconds * (100 - percent) / percent);
        return $"{SourceViewModel.FormatDuration(remaining.TotalSeconds)} remaining";
    }

    private void ShowResult(RemuxResult result)
    {
        HasResult = true;
        ResultSucceeded = result.Outcome == RemuxOutcome.Completed;
        ResultPath = ResultSucceeded ? result.OutputPath : null;
        ResultLogPath = result.LogPath;
        var elapsed = SourceViewModel.FormatDuration(result.Elapsed.TotalSeconds);
        (ResultText, ResultDetail) = result.Outcome switch
        {
            RemuxOutcome.Completed => (
                $"✓ Finished in {elapsed} · {Path.GetFileName(result.OutputPath)}"
                    + (result.OutputBytes is { } bytes ? $" · {SizeEstimator.Format(bytes)}" : ""),
                result.Warnings.Count > 0 ? string.Join(Environment.NewLine, result.Warnings) : null),
            RemuxOutcome.Canceled => ("Canceled. The output file was not changed.", null),
            RemuxOutcome.Failed => ($"✕ {FirstLine(result.Error)}", result.Error),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result.Outcome, null),
        };
        StatusSeverity = result.Outcome switch
        {
            RemuxOutcome.Completed => result.Warnings.Count > 0 ? IssueSeverity.Warning : IssueSeverity.Info,
            RemuxOutcome.Failed => IssueSeverity.Error,
            _ => null,
        };
    }

    private static string FirstLine(string? text) =>
        text?.Split('\n', 2)[0].Trim() is { Length: > 0 } line ? line : "The remux failed.";

    private void UpdateEditable()
    {
        Output.IsEditable = IsEditable;
        Properties.IsEditable = IsEditable;
        foreach (var track in Tracks)
            track.IsEditable = IsEditable;
    }

    partial void OnHasResultChanged(bool value)
    {
        if (!value)
            ShowStatus();
    }

    private async Task SaveFormatAsync(OutputFormat format)
    {
        try
        {
            await _settings.SaveAsync(new FFRuxSettings { LastFormat = format });
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Saving FFRux settings failed");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Saving FFRux settings failed");
        }
    }
}
