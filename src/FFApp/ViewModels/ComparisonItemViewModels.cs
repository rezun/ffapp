using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FFApp.Core.Models;
using FFApp.Helpers;

namespace FFApp.ViewModels;

public sealed partial class ComparisonSampleViewModel : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private double _startSeconds;
    [ObservableProperty] private double _durationSeconds;
    public string Label => double.IsFinite(StartSeconds) && double.IsFinite(DurationSeconds)
        ? $"{FormatHelpers.FormatCompactDuration(StartSeconds)} · {DurationSeconds:F1}s" : "Invalid sample range";
    public ComparisonSampleViewModel(ComparisonSample sample)
    {
        Id = sample.Id;
        _name = sample.Name;
        _startSeconds = sample.StartSeconds;
        _durationSeconds = sample.DurationSeconds;
    }
    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(Label));
    partial void OnStartSecondsChanged(double value) => OnPropertyChanged(nameof(Label));
    partial void OnDurationSecondsChanged(double value) => OnPropertyChanged(nameof(Label));
    public ComparisonSample Snapshot() => new(Id, Name, StartSeconds, DurationSeconds);
}

public sealed partial class ComparisonVariantViewModel : ObservableObject
{
    public string Id { get; }
    [ObservableProperty] private string _name;
    [ObservableProperty] private OutputSettings _settings;
    public string Summary => Describe(Settings);
    public ComparisonVariantViewModel(ComparisonVariant variant)
    {
        Id = variant.Id;
        _name = variant.Name;
        _settings = variant.Settings;
    }
    partial void OnSettingsChanged(OutputSettings value) => OnPropertyChanged(nameof(Summary));
    public ComparisonVariant Snapshot() => new(Id, Name, Settings);

    public static string Describe(OutputSettings settings)
    {
        var quality = settings.EncodingStrategy switch
        {
            EncodingStrategy.TargetQuality => settings.TargetQuality switch
            {
                Av1QualityOptions av1 => $"CRF {av1.Crf} · preset {av1.Preset} · grain {av1.FilmGrain}" + (av1.FilmGrain > 0 ? $" · denoise {av1.FilmGrainDenoise}" : ""),
                H26xQualityOptions h26x => $"CRF {h26x.Crf} · {h26x.Preset}",
                Vp9QualityOptions vp9 => $"quality {vp9.Quality}",
                _ => throw new ArgumentOutOfRangeException(nameof(settings)),
            },
            EncodingStrategy.TargetBitrate => $"{settings.TargetBitrate.VideoBitrate / 1000} kbps",
            EncodingStrategy.TargetFileSize => $"{settings.TargetFileSize.TargetSizeMb} MB target",
            _ => throw new ArgumentOutOfRangeException(nameof(settings)),
        };
        var hdr = settings.HdrMode == HdrMode.TonemapToSdr
            ? " · SDR / " + (settings.ToneMapSettings.Engine == ToneMapEngine.Libplacebo
                ? FormatHelpers.LibplaceboToneMapAlgorithmLabel(settings.ToneMapSettings.LibplaceboAlgorithm)
                : FormatHelpers.ToneMapAlgorithmLabel(settings.ToneMapSettings.Algorithm)) : "";
        return $"{FormatHelpers.VideoCodecLabel(settings.VideoCodec)} · {quality}{hdr}";
    }
}

public sealed record ComparisonChoice(string Id, string Name);

public sealed class ComparisonRowViewModel(string label, IReadOnlyList<ComparisonCellViewModel> cells)
{
    public string Label { get; } = label;
    public IReadOnlyList<ComparisonCellViewModel> Cells { get; } = cells;
}

public sealed partial class ComparisonCellViewModel : ObservableObject
{
    public string SampleId { get; }
    public string VariantId { get; }
    public string Name { get; }
    public ComparisonClip? Clip { get; set; }
    public IRelayCommand SelectCommand { get; }
    [ObservableProperty] private string _status = "Not encoded";
    [ObservableProperty] private string _metrics = "—";
    [ObservableProperty] private string _details = "";
    public ComparisonCellViewModel(string sampleId, string variantId, string name, Action select)
    {
        SampleId = sampleId;
        VariantId = variantId;
        Name = name;
        SelectCommand = new RelayCommand(select);
    }
    public void Complete(ComparisonClip clip, long? baselineSize)
    {
        Clip = clip;
        Status = clip.WasCached ? "Cached" : "Completed";
        var relative = baselineSize is > 0
            ? $" · {(clip.SizeBytes / (double)baselineSize.Value - 1) * 100:+0.0;-0.0;0.0}%" : "";
        Metrics = string.Create(CultureInfo.CurrentCulture,
            $"{clip.SizeBytes / 1048576.0:F2} MiB{relative}\n{clip.ElapsedSeconds:F1}s encoding");
        Details = clip.Path;
    }
}
