using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

public partial class EncodingStrategyViewModel : ObservableObject
{
    [ObservableProperty]
    private EncodingStrategy _selectedStrategy = EncodingStrategy.TargetQuality;

    [ObservableProperty]
    private object? _activeStrategyViewModel;

    public FileSizeViewModel FileSize { get; } = new();
    public BitrateViewModel Bitrate { get; } = new();
    public QualityViewModel Quality { get; } = new();

    public EncodingStrategyViewModel()
    {
        ActiveStrategyViewModel = Quality;
    }

    partial void OnSelectedStrategyChanged(EncodingStrategy value)
    {
        ActiveStrategyViewModel = value switch
        {
            EncodingStrategy.TargetFileSize => FileSize,
            EncodingStrategy.TargetBitrate => Bitrate,
            EncodingStrategy.TargetQuality => Quality,
            _ => Quality,
        };
    }
}
