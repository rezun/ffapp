using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels;

public partial class FormatViewModel : ObservableObject
{
    private bool _isSuppressing;

    [ObservableProperty]
    private VideoMode _selectedVideoMode = VideoMode.Reencode;

    [ObservableProperty]
    private OutputContainer _selectedContainer = OutputContainer.Mp4;

    [ObservableProperty]
    private VideoCodec _selectedVideoCodec = VideoCodec.H264;

    [ObservableProperty]
    private IReadOnlyList<OutputContainer> _availableContainers =
        CompatibilityData.VideoContainers;

    [ObservableProperty]
    private IReadOnlyList<VideoCodec> _availableVideoCodecs =
        CompatibilityData.AllowedVideoCodecs(OutputContainer.Mp4);

    [ObservableProperty]
    private IReadOnlyList<AudioCodec> _availableAudioCodecs =
        CompatibilityData.AllowedAudioCodecs(OutputContainer.Mp4);

    [ObservableProperty]
    private bool _sourceHasVideo = true;

    public bool IsVideoReencode => SelectedVideoMode == VideoMode.Reencode;
    public bool IsVideoVisible => SelectedVideoMode != VideoMode.Remove;

    partial void OnSourceHasVideoChanged(bool value)
    {
        if (!value && SelectedVideoMode != VideoMode.Remove)
            SelectedVideoMode = VideoMode.Remove;
    }

    partial void OnSelectedVideoModeChanged(VideoMode value)
    {
        OnPropertyChanged(nameof(IsVideoReencode));
        OnPropertyChanged(nameof(IsVideoVisible));

        if (_isSuppressing)
            return;

        _isSuppressing = true;
        try
        {
            var newContainers = CompatibilityData.ContainersForVideoMode(value);
            AvailableContainers = newContainers;

            if (!newContainers.Contains(SelectedContainer))
                SelectedContainer = newContainers[0];
            else
                RefreshCodecsForContainer(SelectedContainer);
        }
        finally
        {
            _isSuppressing = false;
        }
    }

    partial void OnSelectedContainerChanged(OutputContainer value)
    {
        if (_isSuppressing)
            return;

        _isSuppressing = true;
        try
        {
            RefreshCodecsForContainer(value);
        }
        finally
        {
            _isSuppressing = false;
        }
    }

    private void RefreshCodecsForContainer(OutputContainer container)
    {
        var videoCodecs = CompatibilityData.AllowedVideoCodecs(container);
        AvailableVideoCodecs = videoCodecs;
        if (videoCodecs.Count > 0 && !videoCodecs.Contains(SelectedVideoCodec))
            SelectedVideoCodec = videoCodecs[0];

        AvailableAudioCodecs = CompatibilityData.AllowedAudioCodecs(container);
    }
}
