using System.Collections.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;

namespace FFApp.ViewModels;

/// <summary>
/// Represents a single source file in the source list.
/// MediaInfo is probed once on add and cached for instant switching.
/// </summary>
public partial class SourceFileViewModel : ObservableObject
{
    public string SourcePath { get; }

    [ObservableProperty]
    private string _displayLabel;

    [ObservableProperty]
    private SourceMediaInfo? _mediaInfo;

    [ObservableProperty]
    private bool _isProbing;

    [ObservableProperty]
    private bool _hasError;

    [ObservableProperty]
    private string? _errorMessage;

    public ImmutableArray<int>? SelectedAudioStreamIndices { get; set; }

    // Per-file trim/crop settings (saved/restored on selection switch)
    [ObservableProperty]
    private double _trimInSeconds;

    [ObservableProperty]
    private double _trimOutSeconds;

    [ObservableProperty]
    private int _cropX;

    [ObservableProperty]
    private int _cropY;

    [ObservableProperty]
    private int _cropW;

    [ObservableProperty]
    private int _cropH;

    // Per-file dirty flags
    [ObservableProperty]
    private bool _isTrimDirty;

    [ObservableProperty]
    private bool _isCropDirty;

    /// <summary>Black-bar crop found by sampling the whole source, if any.</summary>
    [ObservableProperty]
    private CropRect? _suggestedCrop;

    [ObservableProperty]
    private bool _isAnalyzingCrop;

    public SourceFileViewModel(string sourcePath)
    {
        SourcePath = sourcePath;
        _displayLabel = Path.GetFileName(sourcePath);
    }

    /// <summary>
    /// Initialize crop dimensions to match the source video dimensions.
    /// Called after MediaInfo is successfully probed.
    /// </summary>
    public void InitializeFromMediaInfo(SourceMediaInfo mediaInfo)
    {
        MediaInfo = mediaInfo;
        HasError = false;
        ErrorMessage = null;

        var primary = mediaInfo.PrimaryVideoStream;
        CropX = 0;
        CropY = 0;
        CropW = primary?.Width ?? 0;
        CropH = primary?.Height ?? 0;
        TrimInSeconds = 0;
        TrimOutSeconds = mediaInfo.DurationSeconds ?? 0;
    }

    public void SetError(string message)
    {
        HasError = true;
        ErrorMessage = message;
        MediaInfo = null;
    }
}
