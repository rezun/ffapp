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

            // The container change below is suppressed, so refresh the codecs for
            // whichever container ends up selected.
            if (!newContainers.Contains(SelectedContainer))
                SelectedContainer = newContainers[0];
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
        // Audio-only containers have no video codecs. Keep the previous list so the
        // codec ComboBox never sees an empty list: it would clear its selection and
        // write null into the non-nullable SelectedVideoCodec binding.
        var videoCodecs = CompatibilityData.AllowedVideoCodecs(container);
        if (videoCodecs.Count > 0)
        {
            AvailableVideoCodecs = videoCodecs;
            if (!videoCodecs.Contains(SelectedVideoCodec))
                SelectedVideoCodec = videoCodecs[0];
        }

        AvailableAudioCodecs = CompatibilityData.AllowedAudioCodecs(container);
    }
}
