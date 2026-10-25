using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using FFApp.Core;
using FFApp.Core.Models;
using FFApp.Core.Services;
using FFApp.Helpers;
using FFApp.Infrastructure;
using FFApp.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFApp.ViewModels;

public sealed partial class ComparisonViewModel : ObservableObject, IRecipient<OutputSettingsChangedMessage>, IDisposable
{
    private readonly IComparisonService _service;
    private readonly IMessenger _messenger;
    private readonly IDialogService? _dialogs;
    private ComparisonSession? _session;
    private SourceMediaInfo? _media;
    private bool _loadingEditor;
    private bool _previewReady;
    private bool _changingSelection;
    private Func<OutputSettings, bool>? _apply;

    public OutputSettingsViewModel Editor { get; }
    public ComparisonPlaybackViewModel Playback { get; }
    public ObservableCollection<ComparisonSampleViewModel> Samples { get; } = [];
    public ObservableCollection<ComparisonVariantViewModel> Variants { get; } = [];
    public ObservableCollection<ComparisonRowViewModel> Rows { get; } = [];
    public ObservableCollection<ComparisonChoice> Choices { get; } = [];

    [ObservableProperty] private string _sourceLabel = "";
    [ObservableProperty] private string _rangeLabel = "";
    [ObservableProperty] private string _cacheUsageLabel = "Test clips: 0 B";
    [ObservableProperty] private bool _isDeleted;
    [ObservableProperty] private bool _isManagingStorage;
    [NotifyPropertyChangedFor(nameof(HasStatus), nameof(HasFooter))]
    [ObservableProperty] private string _status = "";
    [NotifyPropertyChangedFor(nameof(HasFooter))]
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _sampleCount = ComparisonPlan.DefaultSampleCount;
    [ObservableProperty] private double _sampleDuration = ComparisonPlan.DefaultSampleDuration;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResultTable), nameof(ShowSamplePanel), nameof(PreviewSizeActionLabel))]
    private bool _isPreviewMaximized;
    [ObservableProperty] private ComparisonSampleViewModel? _selectedSample;
    [ObservableProperty] private ComparisonVariantViewModel? _selectedVariant;
    [ObservableProperty] private ComparisonChoice? _leftChoice;
    [ObservableProperty] private ComparisonChoice? _rightChoice;

    public bool CanEdit => !IsRunning && !IsManagingStorage && !IsDeleted;
    public bool HasStatus => !string.IsNullOrWhiteSpace(Status);
    public bool HasFooter => HasStatus || IsRunning;
    public bool IsSamplesStep => SelectedTab == 0;
    public bool IsVariantsStep => SelectedTab == 1;
    public bool IsResultsStep => SelectedTab == 2;
    public bool HasResults => Rows.SelectMany(r => r.Cells).Any(c => c.Clip is not null || c.Status is "Failed" or "Canceled");
    public bool CanShowResults => HasResults || IsRunning;
    public bool ShowResultTable => IsResultsStep && HasResults && !IsPreviewMaximized;
    public bool ShowSamplePanel => !IsPreviewMaximized;
    public string PreviewSizeActionLabel => IsPreviewMaximized ? "Restore panels" : "Maximize videos";
    public int ReadyClipCount => Rows.SelectMany(r => r.Cells).Count(c => c.Clip is not null);
    public int MissingClipCount => Samples.Count * Variants.Count - ReadyClipCount;
    public string CreateClipsLabel => $"Create test clips ({MissingClipCount})";
    public string ClipPlan => $"{Samples.Count} samples × {Variants.Count} variants = {Samples.Count * Variants.Count} short video clips. "
        + $"{ReadyClipCount} ready; {MissingClipCount} to create. The complete movie will not be encoded.";
    public string PrimaryActionLabel => IsSamplesStep ? "Next: choose variants →"
        : IsVariantsStep && MissingClipCount == 0 ? "View results →"
        : IsResultsStep && MissingClipCount == 0 ? $"Apply {SelectedVariant?.Name} to main window" : CreateClipsLabel;
    public string PreviewTitle => SelectedSample is not { } sample ? "Original preview"
        : $"Original · {sample.Name} · {FormatHelpers.FormatCompactDuration(sample.StartSeconds)} · {sample.DurationSeconds:F1}s";
    public double MaximumStart => _session?.RangeEnd ?? 0;
    public double RangeStart => _session?.RangeStart ?? 0;
    public double RangeEnd => _session?.RangeEnd ?? 1;
    public IReadOnlyList<ComparisonSample> TimelineSamples => Samples.Select(s => s.Snapshot()).ToArray();
    public bool IsAv1 => SelectedVariant?.Settings.VideoCodec == VideoCodec.Av1;
    public bool IsHdr => _media?.PrimaryVideoStream?.IsHdr == true;
    public string HdrNote => IsHdr
        ? "HDR previews use an 8-bit RGB display without HDR tone mapping. Compare two SDR variants to judge tone-mapping consistently."
        : "";
    public string VariantDifferences => SelectedVariant is not { } variant || _session is null ? ""
        : ComparisonSettingsDescription.Differences(_session.Baseline, variant.Settings);

    public ComparisonViewModel(IComparisonService service,
        [FromKeyedServices("comparison")] OutputSettingsViewModel editor,
        [FromKeyedServices("comparison")] IMessenger messenger,
        ComparisonPlaybackViewModel playback, IDialogService? dialogs = null)
    {
        _service = service;
        Editor = editor;
        _messenger = messenger;
        Playback = playback;
        _dialogs = dialogs;
        messenger.Register<OutputSettingsChangedMessage>(this);
    }

    public async Task LoadAsync(JobContext context, Func<OutputSettings, bool> apply, CancellationToken ct = default)
    {
        _media = context.MediaInfo;
        _apply = apply;
        _session = await _service.LoadAsync(context, ct);
        _service.ProtectSession(_session);
        RefreshCacheUsage();
        SourceLabel = Path.GetFileName(_session.SourcePath);
        RangeLabel = $"{FormatHelpers.FormatCompactDuration(_session.RangeStart)} – {FormatHelpers.FormatCompactDuration(_session.RangeEnd)} · saved source, trim and crop";
        _loadingEditor = true;
        Editor.UpdateSourceInfo(_media, _session.SelectedAudioStreamIndices);
        _loadingEditor = false;
        foreach (var sample in _session.Samples) AddSampleItem(sample);
        foreach (var variant in _session.Variants) AddVariantItem(variant);
        SelectedSample = Samples.FirstOrDefault();
        SelectedVariant = Variants.FirstOrDefault();
        RefreshChoices();
        LeftChoice = Choices.FirstOrDefault();
        RightChoice = Choices.Skip(1).FirstOrDefault();
        RefreshResults();
        Status = "";
        OnPropertyChanged(nameof(MaximumStart));
        OnPropertyChanged(nameof(RangeStart));
        OnPropertyChanged(nameof(RangeEnd));
        OnPropertyChanged(nameof(IsHdr));
        OnPropertyChanged(nameof(HdrNote));
        NotifyCommands();
    }

    public ComparisonSession Snapshot()
    {
        var session = _session ?? throw new InvalidOperationException("No comparison loaded.");
        var samples = Samples.Select(s => s.Snapshot()).ToArray();
        if (samples.Length == 0 || samples.Any(s => !ComparisonPlan.IsValid(s, session.RangeStart, session.RangeEnd)))
            throw new InvalidOperationException("Every sample needs a positive duration and must stay inside the saved trim range.");
        if (Variants.Count == 0 || Variants.Any(v => v.Settings.VideoMode != VideoMode.Reencode))
            throw new InvalidOperationException("Every variant must re-encode video.");
        return session with { Samples = [.. samples], Variants = [.. Variants.Select(v => v.Snapshot())] };
    }

    private void AddSampleItem(ComparisonSample sample)
    {
        var item = new ComparisonSampleViewModel(sample);
        item.PropertyChanged += OnSampleChanged;
        Samples.Add(item);
    }

    private void AddVariantItem(ComparisonVariant variant)
    {
        var item = new ComparisonVariantViewModel(variant);
        item.PropertyChanged += OnVariantChanged;
        Variants.Add(item);
    }

    private void OnSampleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComparisonSampleViewModel.StartSeconds) or nameof(ComparisonSampleViewModel.DurationSeconds)
            or nameof(ComparisonSampleViewModel.Name))
        {
            RefreshResults();
            RefreshPlayback();
            OnPropertyChanged(nameof(PreviewTitle));
        }
    }

    private void OnVariantChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComparisonVariantViewModel.Settings) or nameof(ComparisonVariantViewModel.Name))
        {
            RefreshChoices();
            RefreshResults();
            RefreshPlayback();
            OnPropertyChanged(nameof(IsAv1));
            OnPropertyChanged(nameof(VariantDifferences));
        }
    }

    partial void OnSelectedSampleChanged(ComparisonSampleViewModel? value)
    {
        OnPropertyChanged(nameof(PreviewTitle));
        RefreshPlayback();
        NotifyCommands();
    }
    partial void OnLeftChoiceChanged(ComparisonChoice? value) => RefreshPlayback();
    partial void OnRightChoiceChanged(ComparisonChoice? value)
    {
        if (IsResultsStep && Variants.FirstOrDefault(v => string.Equals(v.Id, value?.Id, StringComparison.Ordinal)) is { } variant)
            SelectedVariant = variant;
        RefreshPlayback();
    }
    partial void OnSelectedTabChanged(int value)
    {
        IsPreviewMaximized = false;
        OnPropertyChanged(nameof(IsSamplesStep));
        OnPropertyChanged(nameof(IsVariantsStep));
        OnPropertyChanged(nameof(IsResultsStep));
        OnPropertyChanged(nameof(ShowResultTable));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        if (IsVariantsStep) Playback.Pause();
        else RefreshPlayback();
    }
    partial void OnSelectedVariantChanged(ComparisonVariantViewModel? value)
    {
        if (value is not null)
        {
            _loadingEditor = true;
            Editor.ApplySettings(value.Settings);
            _loadingEditor = false;
        }
        OnPropertyChanged(nameof(IsAv1));
        OnPropertyChanged(nameof(VariantDifferences));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        NotifyCommands();
    }

    partial void OnIsRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        NotifyWorkflow();
        NotifyCommands();
    }

    public void Receive(OutputSettingsChangedMessage message)
    {
        if (!_loadingEditor && !IsRunning && SelectedVariant is { } variant)
        {
            variant.Settings = message.Value;
            NotifyCommands();
        }
    }

    private bool CanRun() => CanEdit && _session is not null && Samples.Count > 0 && Variants.Count > 0;
    private bool CanRemoveSample() => CanEdit && SelectedSample is not null && Samples.Count > 1;
    private bool CanRemoveVariant() => CanEdit && SelectedVariant is not null && Variants.Count > 1;
    private bool CanChangeVariant() => CanEdit && SelectedVariant is not null;

    private void NotifyCommands()
    {
        ClearTestClipsCommand.NotifyCanExecuteChanged();
        DeleteComparisonCommand.NotifyCanExecuteChanged();
        RunAllCommand.NotifyCanExecuteChanged();
        RemoveSampleCommand.NotifyCanExecuteChanged();
        RemoveVariantCommand.NotifyCanExecuteChanged();
        DuplicateVariantCommand.NotifyCanExecuteChanged();
        ApplyVariantCommand.NotifyCanExecuteChanged();
        PrimaryActionCommand.NotifyCanExecuteChanged();
        ShowVariantsCommand.NotifyCanExecuteChanged();
    }

    private void NotifyWorkflow()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanShowResults));
        OnPropertyChanged(nameof(ShowResultTable));
        OnPropertyChanged(nameof(ReadyClipCount));
        OnPropertyChanged(nameof(MissingClipCount));
        OnPropertyChanged(nameof(CreateClipsLabel));
        OnPropertyChanged(nameof(ClipPlan));
        OnPropertyChanged(nameof(PrimaryActionLabel));
        ShowResultsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void ShowSamples() => SelectedTab = 0;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void ShowVariants() => SelectedTab = 1;

    [RelayCommand(CanExecute = nameof(CanShowResults))]
    private void ShowResults() => SelectedTab = 2;

    [RelayCommand]
    private void TogglePreviewSize() => IsPreviewMaximized = !IsPreviewMaximized;

    [RelayCommand(CanExecute = nameof(CanEdit))]
    private async Task PrimaryActionAsync()
    {
        if (IsSamplesStep) SelectedTab = 1;
        else if (IsVariantsStep && MissingClipCount == 0) SelectedTab = 2;
        else if (IsResultsStep && MissingClipCount == 0) ApplyVariant();
        else await RunAllCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void Distribute()
    {
        if (!CanEdit || _session is null) return;
        try
        {
            var samples = ComparisonPlan.Distribute(_session.RangeStart, _session.RangeEnd, SampleCount, SampleDuration);
            foreach (var item in Samples) item.PropertyChanged -= OnSampleChanged;
            Samples.Clear();
            foreach (var sample in samples) AddSampleItem(sample);
            SelectedSample = Samples.FirstOrDefault();
            RefreshResults();
            NotifyCommands();
        }
        catch (ArgumentOutOfRangeException ex) { Status = ex.Message; }
    }

    [RelayCommand]
    private void AddSample()
    {
        if (!CanEdit || _session is null || Samples.Count >= ComparisonPlan.MaximumSampleCount) return;
        var start = SelectedSample?.StartSeconds ?? _session.RangeStart;
        start = Math.Clamp(start, _session.RangeStart, Math.Max(_session.RangeStart, _session.RangeEnd - 0.1));
        AddSampleItem(new ComparisonSample(Guid.NewGuid().ToString("N"), "Manual sample", start,
            Math.Min(ComparisonPlan.DefaultSampleDuration, _session.RangeEnd - start)));
        SelectedSample = Samples[^1];
        RefreshResults();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSample))]
    private void RemoveSample()
    {
        if (SelectedSample is not { } item) return;
        item.PropertyChanged -= OnSampleChanged;
        Samples.Remove(item);
        SelectedSample = Samples.FirstOrDefault();
        RefreshResults();
    }

    [RelayCommand(CanExecute = nameof(CanChangeVariant))]
    private void DuplicateVariant()
    {
        if (SelectedVariant is not { } item) return;
        AddVariantItem(new ComparisonVariant(Guid.NewGuid().ToString("N"), item.Name + " copy", item.Settings));
        SelectedVariant = Variants[^1];
        RefreshChoices();
        RefreshResults();
        SelectedTab = 1;
    }

    [RelayCommand(CanExecute = nameof(CanRemoveVariant))]
    private void RemoveVariant()
    {
        if (SelectedVariant is not { } item) return;
        item.PropertyChanged -= OnVariantChanged;
        Variants.Remove(item);
        SelectedVariant = Variants.FirstOrDefault();
        RefreshChoices();
        RefreshResults();
        RefreshPlayback();
    }

    [RelayCommand]
    private void AddGrainVariants()
    {
        if (!CanEdit || SelectedVariant is not { } item || !IsAv1) return;
        foreach (var strength in new[] { 0, 8, 10 })
        {
            var settings = item.Settings;
            settings = settings with
            {
                TargetQuality = settings.TargetQuality is Av1QualityOptions quality
                    ? quality with { FilmGrain = strength, FilmGrainDenoise = 1 } : settings.TargetQuality,
                VideoEncoderOptions = settings.VideoEncoderOptions is Av1EncoderOptions encoder
                    ? encoder with { FilmGrain = strength, FilmGrainDenoise = 1 } : new Av1EncoderOptions(FilmGrain: strength, FilmGrainDenoise: 1),
            };
            if (Variants.Any(v => ComparisonSettingsDescription.NormalizeInactiveGrainDenoising(v.Settings)
                == ComparisonSettingsDescription.NormalizeInactiveGrainDenoising(settings))) continue;
            AddVariantItem(new ComparisonVariant(Guid.NewGuid().ToString("N"), strength == 0 ? "No grain" : $"Grain {strength}", settings));
        }
        RefreshChoices();
        RefreshResults();
        Status = "Grain variants ready: 0 / 8 / 10. Grain 8/10 use source denoising; CRF and other settings are unchanged.";
    }

    [RelayCommand(CanExecute = nameof(CanChangeVariant))]
    private void ApplyVariant()
    {
        if (SelectedVariant is not { } variant) return;
        Status = _apply?.Invoke(variant.Settings) == true
            ? $"Applied {variant.Name} to the source in the main window."
            : "Select this comparison's source in the main window before applying a variant.";
    }

    private void RefreshChoices()
    {
        _changingSelection = true;
        var left = LeftChoice?.Id;
        var right = RightChoice?.Id;
        Choices.Clear();
        Choices.Add(new ComparisonChoice("original", "Original"));
        foreach (var variant in Variants) Choices.Add(new ComparisonChoice(variant.Id, variant.Name));
        LeftChoice = Choices.FirstOrDefault(c => string.Equals(c.Id, left, StringComparison.Ordinal)) ?? Choices.FirstOrDefault();
        RightChoice = Choices.FirstOrDefault(c => string.Equals(c.Id, right, StringComparison.Ordinal)) ?? Choices.Skip(1).FirstOrDefault();
        _changingSelection = false;
    }

    private void RefreshResults()
    {
        OnPropertyChanged(nameof(TimelineSamples));
        Rows.Clear();
        if (_session is null || _media is null) return;
        foreach (var sample in Samples)
        {
            var cells = new List<ComparisonCellViewModel>();
            foreach (var variant in Variants)
            {
                var cell = new ComparisonCellViewModel(sample.Id, variant.Id, variant.Name, () =>
                {
                    _changingSelection = true;
                    SelectedSample = sample;
                    SelectedVariant = variant;
                    RightChoice = Choices.FirstOrDefault(c => string.Equals(c.Id, variant.Id, StringComparison.Ordinal));
                    Playback.Layout = "Side by side";
                    SelectedTab = 2;
                    _changingSelection = false;
                    RefreshPlayback();
                });
                try
                {
                    var key = _service.CacheKey(_session, _media, sample.Snapshot(), variant.Snapshot());
                    var result = _session.Results.FirstOrDefault(r => string.Equals(r.CacheKey, key, StringComparison.Ordinal));
                    if (result is not null && File.Exists(_service.ClipPath(key))
                        && new FileInfo(_service.ClipPath(key)).Length == result.SizeBytes)
                    {
                        var offset = sample.StartSeconds - Math.Max(_session.RangeStart, sample.StartSeconds - ComparisonPlan.PreRollSeconds);
                        cell.Complete(new ComparisonClip(_service.ClipPath(key), offset, sample.DurationSeconds,
                            result.SizeBytes, result.ElapsedSeconds, key, true), cells.FirstOrDefault()?.Clip?.SizeBytes);
                    }
                }
                catch (InvalidOperationException ex) { cell.Status = "Check settings"; cell.Details = ex.Message; }
                catch (ArgumentException ex) { cell.Status = "Check sample"; cell.Details = ex.Message; }
                cells.Add(cell);
            }
            Rows.Add(new ComparisonRowViewModel(sample.Label, cells));
        }
        NotifyWorkflow();
    }

    private void RefreshPlayback()
    {
        if (!_previewReady || _changingSelection || IsVariantsStep || IsManagingStorage || IsDeleted
            || _session is null || _media is null || SelectedSample is not { } sample) return;
        Playback.Reset();
        if (!ComparisonPlan.IsValid(sample.Snapshot(), _session.RangeStart, _session.RangeEnd))
        {
            Playback.Status = "Adjust the sample so it stays inside the saved trim range.";
            return;
        }
        ComparisonTrack? Resolve(ComparisonChoice? choice)
        {
            if (choice?.Id == "original") return new ComparisonTrack(_session.SourcePath, sample.StartSeconds, _session.Crop);
            var clip = Rows.SelectMany(r => r.Cells).FirstOrDefault(c => c.SampleId == sample.Id && c.VariantId == choice?.Id)?.Clip;
            return clip is null ? null : new ComparisonTrack(clip.Path, clip.OffsetSeconds,
                SourceStartSeconds: sample.StartSeconds - clip.OffsetSeconds);
        }
        if (IsSamplesStep)
            Playback.LoadSource(new ComparisonTrack(_session.SourcePath, sample.StartSeconds, _session.Crop),
                sample.DurationSeconds, _media.PrimaryVideoStream?.FrameRate);
        else if (Resolve(LeftChoice) is { } left && Resolve(RightChoice) is { } right)
            Playback.Load(left, right, sample.DurationSeconds, _media.PrimaryVideoStream?.FrameRate);
        else
        {
            Playback.LoadSource(new ComparisonTrack(_session.SourcePath, sample.StartSeconds, _session.Crop),
                sample.DurationSeconds, _media.PrimaryVideoStream?.FrameRate);
        }
    }

    public void PreviewSelection()
    {
        _previewReady = true;
        RefreshPlayback();
    }

    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRun))]
    private async Task RunAllAsync(CancellationToken ct)
    {
        if (_media is null) return;
        try
        {
            var snapshot = Snapshot();
            IsRunning = true;
            SelectedTab = 2;
            Progress = 0;
            Playback.Reset();
            RefreshResults();
            await _service.SaveAsync(snapshot);
            var total = snapshot.Samples.Length * snapshot.Variants.Length;
            var completed = 0;
            foreach (var sample in snapshot.Samples)
            foreach (var variant in snapshot.Variants)
            {
                ct.ThrowIfCancellationRequested();
                var cell = Rows.SelectMany(r => r.Cells).First(c => c.SampleId == sample.Id && c.VariantId == variant.Id);
                Status = $"Encoding {sample.Name} / {variant.Name} ({completed + 1}/{total})…";
                cell.Status = "Encoding…";
                var index = completed;
                var acceptingProgress = true;
                IProgress<JobProgress> progress = new Progress<JobProgress>(p =>
                {
                    if (IsRunning && acceptingProgress && cell.Clip is null)
                    {
                        cell.Status = p.Percentage is { } percent ? $"Encoding {percent:F0}%" : "Encoding…";
                        Progress = (index + ((p.PassIndex - 1 + (p.Percentage ?? 0) / 100) / Math.Max(1, p.PassCount))) / total * 100;
                    }
                });
                try
                {
                    var clip = await _service.EncodeAsync(Snapshot(), _media, sample, variant, progress.Report, ct);
                    var baseline = Rows.First(r => r.Cells.Any(c => c == cell)).Cells[0].Clip?.SizeBytes;
                    cell.Complete(clip, baseline);
                    NotifyWorkflow();
                    _session = (_session ?? snapshot) with
                    {
                        Results = [.. (_session ?? snapshot).Results.Where(r => !(r.SampleId == sample.Id && r.VariantId == variant.Id && r.CacheKey == clip.CacheKey)),
                            new ComparisonResult(sample.Id, variant.Id, clip.CacheKey, clip.SizeBytes, clip.ElapsedSeconds)],
                    };
                    await _service.SaveAsync(Snapshot());
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cell.Status = "Canceled";
                    throw;
                }
                catch (ComparisonEncodingException ex)
                {
                    cell.Status = "Failed";
                    cell.Details = ex.Message;
                }
                finally { acceptingProgress = false; NotifyWorkflow(); }
                completed++;
                Progress = completed * 100.0 / total;
            }
            Status = Rows.SelectMany(r => r.Cells).Any(c => c.Clip is null)
                ? "Finished with failed clips. Hover over a result for details; run again to retry."
                : "Comparison ready. Sizes include up to 2 seconds of pre-roll and exclude audio. Percentages compare with the first variant.";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status = "Canceled. Completed clips are saved and will be reused on the next run.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Comparison encoding failed: {ex}");
            Status = $"Comparison failed: {ex.Message}";
        }
        finally
        {
            IsRunning = false;
            RefreshPlayback();
            RefreshCacheUsage();
        }
    }

    private bool CanManageStorage() => CanEdit && _session is not null;

    partial void OnIsManagingStorageChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEdit));
        NotifyCommands();
    }

    private void RefreshCacheUsage()
    {
        if (_session is null) return;
        var usage = _service.GetCacheUsage(_session.SourcePath);
        CacheUsageLabel = $"Test clips: {FormatHelpers.FormatStorageSize(usage.ComparisonBytes)}";
    }

    [RelayCommand(CanExecute = nameof(CanManageStorage))]
    private async Task ClearTestClipsAsync()
    {
        if (_session is null) return;
        IsManagingStorage = true;
        try
        {
            await Playback.ResetAsync();
            var removed = _service.ClearClips(_session.SourcePath);
            RefreshResults();
            RefreshPlayback();
            Status = $"Deleted {FormatHelpers.FormatStorageSize(removed)} of test clips. Samples and encoding settings are retained.";
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Comparison clip cleanup failed: {ex}");
            RefreshResults();
            Status = $"Could not delete all test clips: {ex.Message}";
        }
        finally
        {
            IsManagingStorage = false;
            RefreshPlayback();
            RefreshCacheUsage();
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageStorage))]
    private async Task DeleteComparisonAsync()
    {
        if (_session is null) return;
        IsManagingStorage = true;
        try
        {
            if (_dialogs is not null && !await _dialogs.ConfirmAsync("Delete comparison",
                    "Delete this comparison's saved samples, encoding variants and test clips? The source video is kept. This window will close.")) return;
            await Playback.ResetAsync();
            _service.DeleteComparison(_session.SourcePath);
            IsDeleted = true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Deleting comparison failed: {ex}");
            RefreshResults();
            Status = $"Could not delete comparison: {ex.Message}";
        }
        finally
        {
            IsManagingStorage = false;
            RefreshPlayback();
        }
    }

    public async Task<bool> TrySaveAsync()
    {
        if (IsDeleted) return true;
        try
        {
            await _service.SaveAsync(Snapshot());
            RefreshCacheUsage();
            Status = "Comparison saved. Completed clips will be reused when reopened.";
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Saving comparison failed: {ex}");
            Status = $"Could not save comparison: {ex.Message}";
            return false;
        }
    }

    public async Task<bool> CloseAsync()
    {
        if (IsManagingStorage) return false;
        _previewReady = false;
        RunAllCommand.Cancel();
        if (RunAllCommand.ExecutionTask is { } running) await running;
        await Playback.ResetAsync();
        return await TrySaveAsync();
    }

    public void Dispose()
    {
        _messenger.UnregisterAll(this);
        foreach (var sample in Samples) sample.PropertyChanged -= OnSampleChanged;
        foreach (var variant in Variants) variant.PropertyChanged -= OnVariantChanged;
        if (_session is not null) _service.ReleaseSession(_session.SourcePath);
        GC.SuppressFinalize(this);
    }
}
