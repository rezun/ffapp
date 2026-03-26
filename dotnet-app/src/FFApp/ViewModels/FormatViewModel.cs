using CommunityToolkit.Mvvm.ComponentModel;
using FFApp.Core.Models;
using FFApp.Core.Validation;

namespace FFApp.ViewModels;

public partial class FormatViewModel : ObservableObject
{
    private bool _isSuppressing;

    [ObservableProperty]
    private OutputContainer _selectedContainer = OutputContainer.Mp4;

    [ObservableProperty]
    private VideoCodec _selectedVideoCodec = VideoCodec.H264;

    [ObservableProperty]
    private IReadOnlyList<OutputContainer> _availableContainers =
        Enum.GetValues<OutputContainer>();

    [ObservableProperty]
    private IReadOnlyList<VideoCodec> _availableVideoCodecs =
        CompatibilityData.AllowedVideoCodecs(OutputContainer.Mp4);

    [ObservableProperty]
    private IReadOnlyList<AudioCodec> _availableAudioCodecs =
        CompatibilityData.AllowedAudioCodecs(OutputContainer.Mp4);

    partial void OnSelectedContainerChanged(OutputContainer value)
    {
        if (_isSuppressing)
            return;

        _isSuppressing = true;
        try
        {
            AvailableVideoCodecs = CompatibilityData.AllowedVideoCodecs(value);
            if (!AvailableVideoCodecs.Contains(SelectedVideoCodec))
                SelectedVideoCodec = AvailableVideoCodecs[0];

            AvailableAudioCodecs = CompatibilityData.AllowedAudioCodecs(value);
        }
        finally
        {
            _isSuppressing = false;
        }
    }
}
